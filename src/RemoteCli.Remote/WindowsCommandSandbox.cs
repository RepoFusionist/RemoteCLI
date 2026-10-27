using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Common.Protocol;
using Microsoft.Win32.SafeHandles;

namespace RemoteCli.Remote;

/// <summary>Runs commands in private LPAC storage with narrowly scoped runtime capabilities and a kill-on-close job.</summary>
public static class WindowsCommandSandbox
{
    private const int MaximumConcurrentSandboxedCommands = 2;
    private const uint JobObjectLimitKillOnClose = 0x2000;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitJobMemory = 0x00000200;
    private const int MaximumProcessesPerSandbox = 16;
    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectCpuRateControlInformation = 15;
    private const uint JobObjectCpuRateControlEnable = 0x1;
    private const uint JobObjectCpuRateControlHardCap = 0x4;
    private const uint CpuRateLimit = 5000; // 50% of total processor cycles per sandbox.
    private static readonly nuint ProcessMemoryLimit = checked((nuint)(768L * 1024 * 1024));
    private static readonly nuint JobMemoryLimit = checked((nuint)(1024L * 1024 * 1024));
    private static readonly SemaphoreSlim ExecutionSlots = new(MaximumConcurrentSandboxedCommands, MaximumConcurrentSandboxedCommands);

    public static Task<CommandExecutionResult> ExecuteAsync(string command, string? directory, TimeSpan timeout,
        string workspace, Func<OutputStreamKind, string, Task> onOutput, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new CommandExecutionResult(RemoteCommandState.Failed, null, "sandbox_requires_windows", "", "unknown"));
        if (!ExecutionSlots.Wait(0))
            return Task.FromResult(new CommandExecutionResult(RemoteCommandState.Failed, null,
                "sandbox_capacity", "", "5.1 (AppContainer)"));
        return ExecuteWithSlotAsync(command, directory, timeout, workspace, onOutput, token);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<CommandExecutionResult> ExecuteWithSlotAsync(string command, string? directory, TimeSpan timeout,
        string workspace, Func<OutputStreamKind, string, Task> onOutput, CancellationToken token)
    {
        try
        {
            return await ExecuteWindowsAsync(command, directory, timeout, workspace, onOutput, token).ConfigureAwait(false);
        }
        finally
        {
            ExecutionSlots.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<CommandExecutionResult> ExecuteWindowsAsync(string command, string? directory,
        TimeSpan timeout, string workspace, Func<OutputStreamKind, string, Task> onOutput, CancellationToken token)
    {
        var scope = new RemoteFileScope(workspace, true);
        var working = scope.Resolve(directory ?? ".", allowRoot: true);
        if (!Directory.Exists(working)) throw new RemoteToolException("working_directory_not_found", "命令工作目录不存在。");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var executable = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var profile = "LongShell.Command." + Guid.NewGuid().ToString("N");
        IntPtr sid = IntPtr.Zero, attributes = IntPtr.Zero, capabilities = IntPtr.Zero, handles = IntPtr.Zero, environment = IntPtr.Zero, packagesPolicy = IntPtr.Zero;
        IntPtr capabilityEntries = IntPtr.Zero;
        var capabilitySids = new List<IntPtr>();
        ProcessInformation child = default;
        SafeFileHandle? outputRead = null, outputWrite = null, errorRead = null, errorWrite = null, inputRead = null, inputWrite = null;
        IntPtr job = IntPtr.Zero;
        var stage = "profile";
        try
        {
            token.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(CreateAppContainerProfile(profile, profile, "LongShell restricted remote command", IntPtr.Zero, 0, out sid));
            var identity = new SecurityIdentifier(sid);
            Marshal.ThrowExceptionForHR(GetAppContainerFolderPath(identity.Value, out var folderPointer));
            string containerFolder;
            try { containerFolder = Marshal.PtrToStringUni(folderPointer) ?? throw new IOException("Missing private container folder."); }
            finally { Marshal.FreeCoTaskMem(folderPointer); }
            stage = "snapshot";
            var snapshot = await SandboxWorkspaceSnapshot.CreateAsync(workspace,
                Path.Combine(containerFolder, "LocalState", "workspace"), token).ConfigureAwait(false);
            // Only the new, disposable copy receives a capability ACL. The real workspace is untouched.
            var privateDirectory = new DirectoryInfo(snapshot.Directory);
            var privateAcl = privateDirectory.GetAccessControl(AccessControlSections.Access);
            privateAcl.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.Modify | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            privateDirectory.SetAccessControl(privateAcl);
            PreparePrivateStorage(snapshot.Directory, containerFolder);
            working = snapshot.MapWorkingDirectory(working);

            stage = "pipes";
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            Check(CreatePipe(out outputRead, out outputWrite, ref security, 0));
            Check(CreatePipe(out errorRead, out errorWrite, ref security, 0));
            Check(CreatePipe(out inputRead, out inputWrite, ref security, 0));
            Check(SetHandleInformation(outputRead, 1, 0));
            Check(SetHandleInformation(errorRead, 1, 0));
            Check(SetHandleInformation(inputWrite, 1, 0));

            nuint size = 0;
            stage = "attributes";
            InitializeProcThreadAttributeList(IntPtr.Zero, 3, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            Check(InitializeProcThreadAttributeList(attributes, 3, 0, ref size));
            // LPAC opts out of ambient ALL APPLICATION PACKAGES file grants.
            packagesPolicy = Marshal.AllocHGlobal(sizeof(int)); Marshal.WriteInt32(packagesPolicy, 1);
            Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x2000F, packagesPolicy, sizeof(int), IntPtr.Zero, IntPtr.Zero));
            foreach (var name in new[] { "registryRead", "lpacCom", "lpacInstrumentation" })
            {
                Check(DeriveCapabilitySidsFromName(name, out var groups, out var groupCount, out var sids, out var sidCount));
                for (var index = 0; index < groupCount; index++) LocalFree(Marshal.ReadIntPtr(groups, index * IntPtr.Size));
                LocalFree(groups);
                for (var index = 0; index < sidCount; index++) capabilitySids.Add(Marshal.ReadIntPtr(sids, index * IntPtr.Size));
                LocalFree(sids);
            }
            var entrySize = Marshal.SizeOf<SidAndAttributes>();
            capabilityEntries = Marshal.AllocHGlobal(entrySize * capabilitySids.Count);
            for (var index = 0; index < capabilitySids.Count; index++)
                Marshal.StructureToPtr(new SidAndAttributes { Sid = capabilitySids[index], Attributes = 4 }, capabilityEntries + index * entrySize, false);
            capabilities = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(new SecurityCapabilities { AppContainerSid = sid, Capabilities = capabilityEntries, CapabilityCount = (uint)capabilitySids.Count }, capabilities, false);
            Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20009, capabilities,
                (nuint)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero));
            handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handles, 0, inputRead.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, outputWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size * 2, errorWrite.DangerousGetHandle());
            Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handles, (nuint)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero));

            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    StdInput = inputRead.DangerousGetHandle(), StdOutput = outputWrite.DangerousGetHandle(), StdError = errorWrite.DangerousGetHandle() },
                AttributeList = attributes
            };
            // Inherit no host credentials or user-defined environment variables.
            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemRoot"] = windows, ["WINDIR"] = windows,
                ["PATH"] = Path.Combine(windows, "System32") + ";" + Path.GetDirectoryName(executable),
                ["TEMP"] = snapshot.Directory, ["TMP"] = snapshot.Directory,
                ["PSModulePath"] = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "Modules")
            };
            foreach (var name in new[] { "SystemDrive", "ComSpec", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
                "CommonProgramFiles", "CommonProgramFiles(x86)", "ProgramData", "USERPROFILE", "LOCALAPPDATA", "APPDATA",
                "USERNAME", "USERDOMAIN", "HOMEDRIVE", "HOMEPATH", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS" })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(value)) variables[name] = value;
            }
            environment = Marshal.StringToHGlobalUni(string.Join('\0', variables.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0");
            var quotedWorking = working.Replace("'", "''");
            var quotedRoot = snapshot.Directory.Replace("'", "''");
            var relativeWorking = Path.GetRelativePath(snapshot.Directory, working);
            var driveWorking = (relativeWorking == "." ? "Workspace:\\" : "Workspace:\\" + relativeWorking).Replace("'", "''");
            var script = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding=[Console]::OutputEncoding; " +
                "$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; New-PSDrive -Name Workspace -PSProvider FileSystem -Root '" + quotedRoot +
                "' -Scope Global | Out-Null; Set-Location -LiteralPath '" + driveWorking + "'; [Environment]::CurrentDirectory='" + quotedWorking + "'; " + command;
            // Feed the script through stdin so the Windows command-line limit cannot truncate a valid request.
            var bootstrap = "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false); & ([scriptblock]::Create([Console]::In.ReadToEnd()))";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
            var commandLine = new StringBuilder('"' + executable + "\" -NoLogo -NoProfile -NonInteractive -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand " + encoded);
            stage = "create_process";
            Check(CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                0x00080000 | 0x00000400 | 0x08000000 | 0x00000004, environment, working, ref startup, out child));
            stage = "job";
            job = CreateJobObject(IntPtr.Zero, null);
            Check(job != IntPtr.Zero);
            var limits = new JobExtendedLimits
            {
                Basic = new JobBasicLimits
                {
                    LimitFlags = JobObjectLimitKillOnClose | JobObjectLimitActiveProcess |
                        JobObjectLimitProcessMemory | JobObjectLimitJobMemory,
                    ActiveProcessLimit = MaximumProcessesPerSandbox
                },
                ProcessMemoryLimit = ProcessMemoryLimit,
                JobMemoryLimit = JobMemoryLimit
            };
            Check(SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JobExtendedLimits>()));
            var cpuLimits = new JobCpuRateControlInformation
            {
                ControlFlags = JobObjectCpuRateControlEnable | JobObjectCpuRateControlHardCap,
                CpuRate = CpuRateLimit
            };
            try
            {
                Check(SetInformationJobObject(job, JobObjectCpuRateControlInformation, ref cpuLimits,
                    (uint)Marshal.SizeOf<JobCpuRateControlInformation>()));
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 50)
            {
                await onOutput(OutputStreamKind.Stderr,
                    "This Windows session cannot enforce the sandbox CPU limit; process and memory limits remain active.")
                    .ConfigureAwait(false);
            }
            Check(AssignProcessToJobObject(job, child.Process));
            using var process = Process.GetProcessById((int)child.ProcessId);
            Check(ResumeThread(child.Thread) != uint.MaxValue);
            stage = "execution";
            outputWrite.Dispose(); errorWrite.Dispose(); inputRead.Dispose();
            using var stdout = new StreamReader(new FileStream(outputRead, FileAccess.Read), new UTF8Encoding(false));
            using var stderr = new StreamReader(new FileStream(errorRead, FileAccess.Read), new UTF8Encoding(false));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            long outputCharacters = 0;
            async Task PumpAsync(StreamReader reader, OutputStreamKind kind)
            {
                var buffer = new char[2048];
                while (true)
                {
                    var count = await reader.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (Interlocked.Add(ref outputCharacters, count) > 8 * 1024 * 1024)
                    { deadline.Cancel(); break; }
                    await onOutput(kind, new string(buffer, 0, count)).ConfigureAwait(false);
                }
            }
            var outputs = Task.WhenAll(PumpAsync(stdout, OutputStreamKind.Stdout), PumpAsync(stderr, OutputStreamKind.Stderr));
            try
            {
                await using (var input = new FileStream(inputWrite, FileAccess.Write))
                    await input.WriteAsync(Encoding.UTF8.GetBytes(script), deadline.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                // Close the job before draining: descendants cannot keep inherited pipes open.
                CloseHandle(job); job = IntPtr.Zero;
                await outputs.WaitAsync(deadline.Token).ConfigureAwait(false);
                if (process.ExitCode == 0) await snapshot.CommitAsync(deadline.Token).ConfigureAwait(false);
                return new(process.ExitCode == 0 ? RemoteCommandState.Succeeded : RemoteCommandState.Failed,
                    process.ExitCode, null, executable, "5.1 (AppContainer)");
            }
            catch (OperationCanceledException)
            {
                CloseHandle(job); job = IntPtr.Zero;
                try { await outputs.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or TimeoutException) { }
                return new(token.IsCancellationRequested ? RemoteCommandState.Cancelled : RemoteCommandState.TimedOut,
                    null, outputCharacters > 8 * 1024 * 1024 ? "output_limit" : token.IsCancellationRequested ? "cancelled" : "timeout",
                    executable, "5.1 (AppContainer)");
            }
        }
        catch (RemoteToolException exception)
        {
            await onOutput(OutputStreamKind.Stderr, exception.Message).ConfigureAwait(false);
            return new(RemoteCommandState.Failed, null, exception.Code, executable, "5.1 (AppContainer)");
        }
        catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException or COMException or IOException)
        {
            // Fail closed. A failed AppContainer launch never retries without isolation.
            var detail = exception is Win32Exception native ? native.NativeErrorCode.ToString() : exception.HResult.ToString("X8");
            await onOutput(OutputStreamKind.Stderr, "Sandbox " + stage + " failed (" + detail + ").").ConfigureAwait(false);
            return new(RemoteCommandState.Failed, null, "sandbox_unavailable_" + stage, executable, "5.1 (AppContainer)");
        }
        finally
        {
            if (child.Process != IntPtr.Zero) { TerminateProcess(child.Process, 1); CloseHandle(child.Process); }
            if (child.Thread != IntPtr.Zero) CloseHandle(child.Thread);
            if (job != IntPtr.Zero) CloseHandle(job);
            outputRead?.Dispose(); outputWrite?.Dispose(); errorRead?.Dispose(); errorWrite?.Dispose(); inputRead?.Dispose(); inputWrite?.Dispose();
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (capabilities != IntPtr.Zero) Marshal.FreeHGlobal(capabilities);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (packagesPolicy != IntPtr.Zero) Marshal.FreeHGlobal(packagesPolicy);
            if (capabilityEntries != IntPtr.Zero) Marshal.FreeHGlobal(capabilityEntries);
            foreach (var capabilitySid in capabilitySids) LocalFree(capabilitySid);
            if (sid != IntPtr.Zero) { FreeSid(sid); DeleteAppContainerProfile(profile); }
        }
    }

    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    [SupportedOSPlatform("windows")]
    private static void PreparePrivateStorage(string privateCopy, string containerFolder)
    {
        // This is newly created per-command storage, not the user's source directory.
        var expected = Path.GetFullPath(Path.Combine(containerFolder, "LocalState", "workspace"));
        if (!string.Equals(Path.GetFullPath(privateCopy), expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Sandbox storage path does not belong to this private container.");
        Check(ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;OICI;NW;;;LW)", 1, out var descriptor, out _));
        try
        {
            Check(GetSecurityDescriptorSacl(descriptor, out _, out var label, out _));
            var error = SetNamedSecurityInfo(privateCopy, 1, 0x10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, label);
            if (error != 0) throw new Win32Exception((int)error);
        }
        finally { LocalFree(descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities { public IntPtr AppContainerSid, Capabilities; public uint CapabilityCount, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    { public int Size; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags; public ushort ShowWindow, ReservedSize; public IntPtr ReservedBytes, StdInput, StdOutput, StdError; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JobBasicLimits { public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobExtendedLimits { public JobBasicLimits Basic; public IoCounters Io; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential)] private struct JobCpuRateControlInformation { public uint ControlFlags, CpuRate; }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr capabilities, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int GetAppContainerFolderPath(string sid, out IntPtr path);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("advapi32.dll", EntryPoint = "SetNamedSecurityInfoW", CharSet = CharSet.Unicode)] private static extern uint SetNamedSecurityInfo(string path, int type, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out IntPtr securityDescriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr sacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeriveCapabilitySidsFromName(string name, out IntPtr groups, out int groupCount, out IntPtr sids, out int sidCount);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JobExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetInformationJobObject")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JobCpuRateControlInformation limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
