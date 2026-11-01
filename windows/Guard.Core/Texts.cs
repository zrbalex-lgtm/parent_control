namespace ParentControl.Core;

/// <summary>User-facing strings shown on the child's PC (neutral wording, no family roles).</summary>
public static class Texts
{
    public const string Title = "Экранное время";

    public static string Lock(LockReason reason, string? windowFrom, string? windowTo) => reason switch
    {
        LockReason.Limit => "Время за компьютером на сегодня закончилось.",
        LockReason.Window => $"Сейчас компьютер недоступен.\nРазрешённое время: {windowFrom}–{windowTo}.",
        _ => "Компьютер заблокирован.",
    };

    public const string LockFooter = "Сейчас будет выход на экран входа. Программы останутся открытыми.";

    public static string Warn(int minutesLeft) => minutesLeft <= 1
        ? "Осталась 1 минута! Сохрани игру — скоро выход на экран входа."
        : $"Осталось {minutesLeft} мин. Сохрани игру заранее.";

    public static string RequestResult(RequestInfo r) => r.Status == RequestStatuses.Approved
        ? $"Добавлено {r.GrantedMinutes ?? r.Minutes} мин."
        : $"Запрос на {r.Minutes} мин отклонён.";

    public const string RequestSent = "Запрос отправлен — ждём ответа.";
    public const string RequestAlreadyPending = "Запрос уже отправлен — ждём ответа.";

    /// <summary>Explains which rule the countdown follows.</summary>
    public static string RemainingBy(string? by, string? windowTo) => by switch
    {
        RemainingReasons.Window => $"до конца разрешённого времени ({windowTo})",
        RemainingReasons.Limit => "по дневному лимиту",
        RemainingReasons.Lock => "до блокировки",
        _ => "",
    };

    public static string Remaining(int? minutes) => minutes is { } m
        ? $"{m / 60}:{m % 60:00}"
        : "без ограничений";
}
