using System.ComponentModel.DataAnnotations;

namespace Aurum.App.Infrastructure.Pricing.Macro;

public sealed class BlsOptions
{
    public const string SectionName = "Macro:Bls";
    [Required] public Uri? BaseUrl { get; set; }
    [Required(AllowEmptyStrings = false)] public string ApiKey { get; set; } = string.Empty;
}
