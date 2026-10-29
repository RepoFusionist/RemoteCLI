import { createHighlighterCoreSync, createCssVariablesTheme } from "shiki/core";
import { createJavaScriptRegexEngine } from "shiki/engine/javascript";
import csharp from "@shikijs/langs/csharp";
import css from "@shikijs/langs/css";
import diff from "@shikijs/langs/diff";
import html from "@shikijs/langs/html";
import javascript from "@shikijs/langs/javascript";
import json from "@shikijs/langs/json";
import jsonc from "@shikijs/langs/jsonc";
import jsx from "@shikijs/langs/jsx";
import markdown from "@shikijs/langs/markdown";
import powershell from "@shikijs/langs/powershell";
import python from "@shikijs/langs/python";
import shellscript from "@shikijs/langs/shellscript";
import sql from "@shikijs/langs/sql";
import tsx from "@shikijs/langs/tsx";
import typescript from "@shikijs/langs/typescript";
import xml from "@shikijs/langs/xml";
import yaml from "@shikijs/langs/yaml";

(function () {
  "use strict";

  var ALIASES = {
    "c#": "csharp", cs: "csharp", csx: "csharp", csharp: "csharp",
    ts: "typescript", typescript: "typescript",
    js: "javascript", javascript: "javascript", mjs: "javascript", cjs: "javascript",
    jsx: "jsx", tsx: "tsx",
    json: "json", jsonc: "jsonc",
    ps1: "powershell", psm1: "powershell", pwsh: "powershell", powershell: "powershell",
    sh: "shellscript", bash: "shellscript", zsh: "shellscript", shell: "shellscript",
    shellscript: "shellscript", console: "shellscript",
    py: "python", python: "python",
    html: "html", htm: "html",
    css: "css", scss: "css", less: "css",
    xml: "xml", svg: "xml", plist: "xml", csproj: "xml", xaml: "xml",
    yml: "yaml", yaml: "yaml",
    sql: "sql",
    md: "markdown", markdown: "markdown",
    diff: "diff", patch: "diff"
  };

  var highlighter = createHighlighterCoreSync({
    themes: [createCssVariablesTheme({ name: "css-variables", variablePrefix: "--shiki-", fontStyle: true })],
    langs: [csharp, css, diff, html, javascript, json, jsonc, jsx, markdown, powershell, python,
      shellscript, sql, tsx, typescript, xml, yaml],
    engine: createJavaScriptRegexEngine()
  });

  function languageFor(label) {
    var key = String(label || "").replace(/^language-/i, "").trim().toLowerCase();
    return ALIASES[key] || null;
  }

  function highlight(code, label) {
    var language = languageFor(label);
    if (!language) return null;
    try {
      var result = highlighter.codeToTokens(String(code || ""), { lang: language, theme: "css-variables" });
      var lines = result.tokens.map(function (line) {
        return line.map(function (token) {
          return { text: token.content, color: token.color || "", fontStyle: token.fontStyle || 0 };
        });
      });
      while (lines.length && lines[lines.length - 1].length === 0) lines.pop();
      return { language: language, lines: lines };
    } catch (error) {
      return null;
    }
  }

  window.LongShellPlugins = window.LongShellPlugins || {};
  window.LongShellPlugins["code-highlight"] = {
    api: 1,
    version: "1.0.0",
    highlight: highlight,
    languages: Object.keys(ALIASES)
  };

  try {
    document.dispatchEvent(new CustomEvent("longshell-plugin-ready", { detail: { id: "code-highlight" } }));
  } catch (error) { }
})();
