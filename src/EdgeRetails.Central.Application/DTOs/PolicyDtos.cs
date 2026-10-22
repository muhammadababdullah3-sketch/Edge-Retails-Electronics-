using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EdgeRetails.Central.Application.DTOs;

public class ConfigurePolicyCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("policy")]
    [Required]
    public PolicyDto Policy { get; set; } = null!;
}


