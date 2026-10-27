using System.Globalization;
using System.Text;

namespace ResourceManager.App.Application.Monitoring;

internal sealed class MonitoringConditionParseException(int position, string reason)
    : ArgumentException($"Condition at position {position}: {reason}.")
{
    public int Position { get; } = position;
    public string Reason { get; } = reason;
}

// A bounded, one-pass expansion. It owns no rule state, sampling or evaluator.
internal static class MonitoringConditionLanguage
{
    internal static Task<ExpandedMonitoringHook> ExpandAsync(string text, string id, MonitoringConditionVocabulary vocabulary,
        Func<int, Task<string?>> resolveStartKey)
        => new Parser(text, vocabulary, resolveStartKey).ExpandAsync(id);

    private sealed class Parser(string text, MonitoringConditionVocabulary vocabulary, Func<int, Task<string?>> resolveStartKey)
    {
        private int at;
        private MonitoringConditionParseException Error(string reason, int? position = null) => new(position ?? at, reason);

        internal async Task<ExpandedMonitoringHook> ExpandAsync(string id)
        {
            if (text.Length > vocabulary.MaximumLength) throw Error("condition is too long", vocabulary.MaximumLength);
            for (var index = 0; index < text.Length; index++)
                if (char.IsControl(text[index]) || char.IsWhiteSpace(text[index]) && text[index] != ' ')
                    throw Error("only one line and ASCII spaces are allowed", index);
            Spaces();
            var subjectAt = at;
            var subject = Word();
            if (subject == "memory-protection")
            {
                Space();
                var eventAt = at;
                var eventName = Word();
                if (!MonitoringConditionVocabulary.MemoryProtectionEvents.Contains(eventName, StringComparer.Ordinal))
                    throw Error("unknown memory-protection event", eventAt);
                Spaces();
                if (at != text.Length) throw Error("expected end of event condition");
                return new(id, 0, null) { Event = "memory-protection." + eventName };
            }
            var binding = "machine";
            string? software = null, device = null, startKey = null;
            int? pid = null;
            if (subject is "process" or "software" or "device")
            {
                binding = subject;
                Space();
                if (binding == "process")
                {
                    var pidAt = at;
                    pid = PositiveInt("process PID");
                    if (at < text.Length && text[at] == ':')
                    {
                        at++;
                        var keyAt = at;
                        var keyText = Digits();
                        if (!ulong.TryParse(keyText, NumberStyles.None, CultureInfo.InvariantCulture, out var key) || key == 0)
                            throw Error("start key must be a positive UInt64", keyAt);
                        startKey = key.ToString(CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        if (at < text.Length && text[at] != ' ') throw Error("expected a space or colon after PID");
                        startKey = await resolveStartKey(pid.Value)
                            ?? throw Error("published process start key is unavailable; supply PID:STARTKEY", pidAt);
                    }
                }
                else if (binding == "software") software = Quoted();
                else device = Quoted();
                Space(); subjectAt = at; subject = Word();
            }
            var name = vocabulary.Names.FirstOrDefault(item => item.Binding == binding && item.Name == subject
                && (binding != "device" || item.DeviceIdentity == device))
                ?? throw Error("unknown name or bound device", subjectAt);
            Space();
            var operatorAt = at;
            var symbol = Word();
            var comparison = vocabulary.Operators.FirstOrDefault(item => item.Spelling == symbol)
                ?? throw Error("unsupported comparison operator", operatorAt);
            Space();
            var thresholdAt = at;
            var threshold = Amount(name.Unit);
            if (comparison.IncreaseThreshold)
            {
                threshold = Math.BitIncrement(threshold);
                if (!double.IsFinite(threshold)) throw Error("strict threshold exceeds the low-level numeric range", thresholdAt);
            }
            Space(); Keyword("for"); Space();
            var holdAt = at;
            var hold = Amount("time");
            if (hold <= 0 || hold >= TimeSpan.MaxValue.TotalSeconds || TimeSpan.FromSeconds(hold) <= TimeSpan.Zero)
                throw Error("hold time must be positive and representable", holdAt);
            var interval = vocabulary.Defaults.IntervalMs;
            var top = vocabulary.Defaults.TopOwners;
            Spaces();
            if (NextWordIs("every"))
            {
                Keyword("every"); Space(); var intervalAt = at;
                _ = Amount("time", out var seconds);
                if (seconds < .001m || seconds > (decimal)int.MaxValue / 1000)
                    throw Error("frequency must be a positive whole Int32 millisecond count", intervalAt);
                var milliseconds = seconds!.Value * 1000;
                if (milliseconds < 1 || milliseconds > int.MaxValue || milliseconds != decimal.Truncate(milliseconds))
                    throw Error("frequency must be a positive whole Int32 millisecond count", intervalAt);
                interval = (int)milliseconds;
                Spaces();
            }
            if (NextWordIs("top"))
            {
                Keyword("top"); Space(); var topAt = at;
                var digits = Digits();
                if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out top))
                    throw Error("owner count must be a nonnegative Int32", topAt);
                if (!name.SupportsOwners && top > 0) throw Error("this object has no top-owner list", topAt);
                Spaces();
            }
            if (at != text.Length) throw Error("unexpected or repeated clause; expected end of condition");
            return new(id, interval, new(binding, name.MetricId, name.Quantity, comparison.Comparison, threshold,
                hold, top, software, pid, startKey, device));
        }

        private double Amount(string unit) => Amount(unit, out _);
        private double Amount(string unit, out decimal? exactSeconds)
        {
            exactSeconds = null;
            var numberAt = at;
            _ = Digits();
            if (at < text.Length && text[at] == '.') { at++; _ = Digits(); }
            if (at < text.Length && text[at] is 'e' or 'E') throw Error("exponents are not allowed");
            var unitAt = at;
            if (!double.TryParse(text[numberAt..unitAt], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
                || !double.IsFinite(number)) throw Error("number exceeds the low-level numeric range", numberAt);
            while (at < text.Length && text[at] != ' ') at++;
            var spelling = text[unitAt..at];
            var accepted = vocabulary.Units.First(item => item.Unit == unit).Spellings.FirstOrDefault(item => item.Spelling == spelling)
                ?? throw Error("unsupported unit for this value", unitAt);
            if (unit == "time")
            {
                if (!decimal.TryParse(text[numberAt..unitAt], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var exact))
                    throw Error("time exceeds the low-level duration range", numberAt);
                exactSeconds = exact * (decimal)accepted.Scale;
                return (double)exactSeconds.Value;
            }
            var result = number * accepted.Scale;
            if (!double.IsFinite(result)) throw Error("converted number exceeds the low-level numeric range", numberAt);
            return result;
        }

        private string Digits()
        {
            var start = at;
            while (at < text.Length && char.IsAsciiDigit(text[at])) at++;
            if (start == at) throw Error("expected decimal digits");
            return text[start..at];
        }
        private int PositiveInt(string label)
        {
            var start = at; var value = Digits();
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
                ? number : throw Error(label + " must be a positive Int32", start);
        }
        private string Word()
        {
            var start = at;
            while (at < text.Length && text[at] != ' ') at++;
            if (start == at) throw Error("expected a token");
            return text[start..at];
        }
        private bool NextWordIs(string word) => text.AsSpan(at).StartsWith(word, StringComparison.Ordinal)
            && (at + word.Length == text.Length || text[at + word.Length] == ' ');
        private void Keyword(string keyword)
        {
            var start = at;
            if (Word() != keyword) throw Error("expected " + keyword, start);
        }
        private void Spaces() { while (at < text.Length && text[at] == ' ') at++; }
        private void Space()
        {
            if (at >= text.Length || text[at] != ' ') throw Error("expected an ASCII space");
            Spaces();
        }
        private string Quoted()
        {
            var start = at;
            if (at >= text.Length || text[at] != '"') throw Error("expected a quoted identity");
            at++;
            var result = new StringBuilder();
            while (at < text.Length)
            {
                var character = text[at++];
                if (character == '"')
                {
                    var identity = result.ToString();
                    if (identity.Length == 0 || string.IsNullOrWhiteSpace(identity)) throw Error("identity must not be empty", start);
                    if (identity != identity.Trim()) throw Error("identity must not have leading or trailing whitespace", start);
                    return identity;
                }
                if (character == '\\')
                {
                    if (at >= text.Length) throw Error("unterminated quoted identity", start);
                    character = text[at++];
                    if (character is not ('"' or '\\')) throw Error("unsupported identity escape", at - 1);
                }
                result.Append(character);
            }
            throw Error("unterminated quoted identity", start);
        }
    }
}
