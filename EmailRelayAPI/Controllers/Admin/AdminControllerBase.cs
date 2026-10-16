using System.Text;
using EmailRelayAPI.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EmailRelayAPI.Controllers.Admin;

/// <summary>
/// CSRF defence for cookie-authenticated admin calls: state-changing requests must carry
/// "X-Requested-With: XMLHttpRequest" (browsers never add it cross-site without CORS approval).
/// </summary>
public class RequireAjaxHeaderAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return;

        var authType = context.HttpContext.User.FindFirst(RelayClaims.AuthType)?.Value;
        var isCookie = authType == "admin" || context.HttpContext.User.Identity?.IsAuthenticated != true;
        if (!isCookie) return; // API-key callers are not vulnerable to CSRF

        var header = context.HttpContext.Request.Headers["X-Requested-With"].ToString();
        if (!string.Equals(header, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
        {
            context.Result = new ObjectResult(Models.ApiResponse.Fail("Missing X-Requested-With header")) { StatusCode = StatusCodes.Status403Forbidden };
        }
    }
}

/// <summary>Base for the admin console API: cookie (AuthenAPI login) or admin-scoped API key.</summary>
[ApiController]
[Produces("application/json")]
[Authorize(AuthenticationSchemes = RelayClaims.AdminCookieScheme + "," + RelayClaims.ApiKeyScheme, Policy = RelayClaims.PolicyViewer)]
[RequireAjaxHeader]
[ApiExplorerSettings(GroupName = "admin")]
public abstract class AdminControllerBase : ControllerBase
{
    protected string Actor => User.Identity?.Name ?? "unknown";
    protected string ClientIp => HttpContext.GetClientIp();
    protected bool IsAdmin => User.IsInRole(RelayClaims.RoleAdmin);

    protected static (int Page, int PageSize) Paging(int page, int pageSize, int max = 200)
        => (Math.Max(1, page), Math.Clamp(pageSize, 1, max));

    protected FileContentResult Csv(string fileName, IEnumerable<string> header, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", header.Select(CsvEscape)));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", row.Select(v => CsvEscape(Format(v)))));
        }
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", fileName);
    }

    private static string Format(object? v) => v switch
    {
        null => string.Empty,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss"),
        bool b => b ? "true" : "false",
        _ => v.ToString() ?? string.Empty
    };

    private static string CsvEscape(string s)
    {
        if (s.Length > 0 && (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@')) s = "'" + s; // formula injection guard
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
