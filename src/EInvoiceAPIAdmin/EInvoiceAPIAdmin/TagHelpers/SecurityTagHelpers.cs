using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System.Text.RegularExpressions;

namespace InvoiceWebApp.TagHelpers;

[HtmlTargetElement("script")]
public sealed class ScriptNonceTagHelper : TagHelper
{
    [ViewContext, HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.SetAttribute("nonce", ViewContext.HttpContext.Items["CspNonce"]?.ToString());
    }
}

[HtmlTargetElement("button", Attributes = "onclick")]
public sealed class ConfirmationTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var handler = output.Attributes["onclick"].Value.ToString() ?? string.Empty;
        var match = Regex.Match(handler, "^return confirm\\('([^']*)'\\);?$");
        if (!match.Success) throw new InvalidOperationException("Replace inline button JavaScript with an event listener.");
        output.Attributes.RemoveAll("onclick");
        output.Attributes.SetAttribute("data-confirm", match.Groups[1].Value);
    }
}
