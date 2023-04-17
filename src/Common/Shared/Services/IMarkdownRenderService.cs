using System;
using System.Collections.Generic;

namespace Meshmakers.Octo.Common.Shared.Services;

public interface IMarkdownRenderService
{
    string RenderPlainText(string markdown, Dictionary<string, Func<string>> replaceRules);

    string RenderHtml(string markdown, Dictionary<string, Func<string>> replaceRules);
}
