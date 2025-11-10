using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GarminTempApi.Models;

public class ActivityDetailSnapshot
{
    public int Id { get; set; }

    [Required]
    public int ActivityId { get; set; }

    [ForeignKey(nameof(ActivityId))]
    public Activity Activity { get; set; } = null!;

    [Required]
    public string DetailJson { get; set; } = string.Empty;

    public DateTime LastUpdatedUtc { get; set; }
}
