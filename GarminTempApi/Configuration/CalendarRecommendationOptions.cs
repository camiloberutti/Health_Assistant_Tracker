using System.ComponentModel.DataAnnotations;

namespace GarminTempApi.Configuration;

public sealed class CalendarRecommendationOptions
{
    private const int DefaultPreparationDays = 30;
    private const int DefaultTaperDays = 7;
    private const int DefaultRecoveryDays = 7;
    private const int DefaultRaceLookaheadDays = 30;

    [Range(1, 180)]
    public int RacePreparationWindowDays { get; set; } = DefaultPreparationDays;

    [Range(1, 30)]
    public int RaceTaperWindowDays { get; set; } = DefaultTaperDays;

    [Range(1, 30)]
    public int RaceRecoveryWindowDays { get; set; } = DefaultRecoveryDays;

    [Range(1, 90)]
    public int RaceLookaheadDays { get; set; } = DefaultRaceLookaheadDays;
}
