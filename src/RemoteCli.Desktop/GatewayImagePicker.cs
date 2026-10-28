namespace RemoteCli.Desktop;

internal sealed partial class MainForm
{
    private async Task SelectGatewayImagesAsync()
    {
        using var picker = new OpenFileDialog
        {
            Title = "选择要发送给模型的图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.webp",
            Multiselect = true,
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var images = await GatewayImages.LoadFilesAsync(picker.FileNames, _serviceLifetime.Token);
            PostToUi(new { type = "gateway-images-selected", images = images.Select(image => new { id = Guid.NewGuid().ToString("N"), name = image.Name, mime = image.MediaType, dataUrl = image.DataUrl }).ToArray() });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { PostToUi(new { type = "toast", text = "图片无法加载：" + ex.Message }); }
    }
}
