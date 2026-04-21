namespace EBot.WebHost.Services;

/// <summary>
/// All Discord notification settings. Stored as data/discord-settings.json,
/// returned by GET /api/discord/status, and accepted by POST /api/discord/settings.
/// Every property has a sensible default so a missing key in the JSON file is harmless.
/// </summary>
public class DiscordSettings
{
    // ─── Connection ──────────────────────────────────────────────────────────

    public string WebhookUrl { get; set; } = DiscordNotificationService.DefaultWebhookUrl;
    public bool   Enabled    { get; set; } = true;

    // ─── Event toggles ────────────────────────────────────────────────────────

    /// Post when the bot starts or stops.
    public bool NotifyBotStartStop  { get; set; } = true;
    /// Post after every unload cycle.
    public bool NotifyUnloadCycle   { get; set; } = true;
    /// Post when the bot warps to a different belt.
    public bool NotifyBeltChange    { get; set; } = true;
    /// Post when an asteroid belt is marked depleted.
    public bool NotifyBeltDepleted  { get; set; } = true;
    /// Post when the ship docks at or undocks from a station.
    public bool NotifyDockUndock    { get; set; } = false;
    /// Post a mining summary on a fixed interval.
    public bool NotifyPeriodicSummary { get; set; } = true;
    /// Post on emergency stop.
    public bool NotifyEmergencyStop { get; set; } = true;
    /// Post when the shield drops below the escape threshold.
    public bool NotifyShieldEscape  { get; set; } = true;

    // ─── Cycle report fields ─────────────────────────────────────────────────

    public bool CycleShowM3Gained    { get; set; } = true;
    public bool CycleShowTotalM3     { get; set; } = true;
    public bool CycleShowCycleCount  { get; set; } = true;
    public bool CycleShowRate        { get; set; } = true;
    public bool CycleShowHomeStation { get; set; } = false;

    // ─── Summary report fields ────────────────────────────────────────────────

    public int  SummaryIntervalMinutes { get; set; } = 10;
    public bool SummaryShowElapsed     { get; set; } = true;
    public bool SummaryShowCycles      { get; set; } = true;
    public bool SummaryShowTotalM3     { get; set; } = true;
    public bool SummaryShowRate        { get; set; } = true;
    public bool SummaryShowStatus      { get; set; } = true;
    public bool SummaryShowBeltInfo    { get; set; } = true;
    public bool SummaryShowSystemName  { get; set; } = true;

    // ─── Formatting ──────────────────────────────────────────────────────────

    /// Prepended to every Discord message. Useful for a server name or emoji tag.
    public string MessagePrefix      { get; set; } = "";
    /// Discord mention string appended to emergency-stop messages, e.g. "@here" or "<@123456789>".
    public string EmergencyMention   { get; set; } = "";
}
