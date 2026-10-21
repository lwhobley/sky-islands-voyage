using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading.Tasks;
using SkyIslands.Backend;
using SkyIslands.Core;

namespace SkyIslands.UI
{
    /// <summary>
    /// Plain C# view model: views re-read state on <see cref="Changed"/>. Async commands run
    /// through <see cref="Run"/>, which guards re-entry and turns failures into toasts.
    /// </summary>
    public abstract class ViewModelBase : IDisposable
    {
        public event Action Changed;
        public event Action<string> Toast;
        public event Action<string> Celebrate;

        public bool IsBusy { get; private set; }

        public virtual void Dispose() { }

        protected void NotifyChanged() => Changed?.Invoke();
        protected void ShowToast(string message) => Toast?.Invoke(message);
        protected void ShowCelebration(string message) => Celebrate?.Invoke(message);

        protected async void Run(Func<Task> command)
        {
            if (IsBusy) return;
            IsBusy = true;
            NotifyChanged();
            try
            {
                await command();
            }
            catch (Exception e)
            {
                ShowToast(UiText.Error(e));
            }
            finally
            {
                IsBusy = false;
                NotifyChanged();
            }
        }
    }

    public static class UiText
    {
        static readonly string[] Suffixes = { "", "K", "M", "B", "T", "Qa", "Qi", "Sx", "Sp", "Oc", "No", "Dc" };

        static readonly Dictionary<string, string> Errors = new Dictionary<string, string>
        {
            ["network_error"] = "You're offline. Your rolls are saved; try again soon.",
            ["insufficient_crystals"] = "Not enough crystals yet.",
            ["structure_damaged"] = "Repair it before upgrading.",
            ["no_attack_available"] = "Land a Wind-Cannon to raid.",
            ["revenge_unavailable"] = "That revenge has already been taken.",
            ["no_card_packs"] = "No card packs. Storm Spirits bring them.",
            ["no_active_season"] = "The next card season starts soon.",
            ["insufficient_dust"] = "Not enough dust to craft that card.",
            ["card_already_owned"] = "You already own that card.",
            ["already_in_guild"] = "You're already in a guild.",
            ["guild_full"] = "That guild is full.",
            ["guild_closed"] = "That guild is invite-only.",
            ["guild_name_taken"] = "That guild name or tag is taken.",
            ["invalid_guild_details"] = "Names need 3–24 characters; tags 2–5 letters or digits.",
            ["not_in_guild"] = "You're not in a guild.",
            ["insufficient_guild_role"] = "Only the leader or officers can do that.",
            ["rate_limited"] = "Easy there, captain. Wait a few seconds.",
            ["invalid_message"] = "Messages need 1–500 characters.",
            ["invalid_display_name"] = "Names need 3–20 characters, without symbols.",
            ["name_not_allowed"] = "That name isn't allowed. Try another.",
        };

        /// <summary>1234 → "1.23K", 1.43e20 → "143Qi".</summary>
        public static string Short(BigInteger value)
        {
            if (value < 1000) return value.ToString(CultureInfo.InvariantCulture);
            int digits = value.ToString(CultureInfo.InvariantCulture).Length;
            int group = Math.Min((digits - 1) / 3, Suffixes.Length - 1);
            // Keep two extra digits, then scale down as a double for formatting.
            double scaled = (double)(value / BigInteger.Pow(10, group * 3 - 2)) / 100.0;
            // Truncate (never round up), so 999,999 reads "999K" rather than "1000K".
            double step = scaled >= 100 ? 1 : scaled >= 10 ? 10 : 100;
            return (Math.Floor(scaled * step) / step).ToString("0.##", CultureInfo.InvariantCulture) + Suffixes[group];
        }

        public static string Countdown(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

        public static string Outcome(RollOutcome outcome) => outcome switch
        {
            RollOutcome.Crystals => "Crystals",
            RollOutcome.WindCannon => "Cannon",
            RollOutcome.CloudShield => "Shield",
            RollOutcome.StormSpirit => "Storm",
            RollOutcome.AetherBattery => "Battery",
            _ => "?",
        };

        public static string Error(Exception e)
        {
            if (e is SupabaseException s)
            {
                // Network failures carry the code; server errors carry the raised text.
                string key = s.Status == 0 ? s.Code : s.Message;
                if (key != null && Errors.TryGetValue(key, out var friendly)) return friendly;
            }
            UnityEngine.Debug.LogWarning($"[UI] {e}");
            return "Something went wrong. Please try again.";
        }
    }
}
