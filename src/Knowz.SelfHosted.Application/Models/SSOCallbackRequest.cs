namespace Knowz.SelfHosted.Application.Models;

public class SSOCallbackRequest
{
    public string Code { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}
