using System.Text.RegularExpressions;
using Ripple.Services.Adapters;

namespace Ripple.Services;

/// <summary>
/// Walks an adapter's <see cref="ModeSpec"/> graph against a snapshot
/// of recent terminal output and decides which mode the REPL is
/// currently in. Pure function, no state — the caller (ConsoleWorker)
/// holds the most-recent result and re-evaluates after every command.
///
/// The decision rule is intentionally simple: scan the candidate
/// modes in declaration order (skipping the default), check each
/// one's <c>detect</c> regex against the tail of the recent output,
/// and return the first match. If no auto_enter mode matches, fall
/// back to the mode flagged <c>default: true</c>, or to the first
/// mode in the list. Mode declarations with <c>nested: true</c>
/// can also report a level via the <c>level_capture</c> regex
/// group, but the basic detector only returns the name — level
/// extraction is an opt-in second pass.
///
/// Schema §9 + §18 Q2 runtime gap. Before this, the mode graph was
/// declarative-only and AdapterDeclaredTestsRunner treated
/// <c>expect_mode</c> / <c>expect_level</c> as deferred fields.
/// </summary>
public static class ModeDetector
{
    // Adapter-supplied detect/primary patterns are external input. Cap
    // per-match runtime so a pathological pattern in a user-dropped
    // ~/.ripple/adapters/*.yaml can't pin the worker on every prompt.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Inspect the tail of <paramref name="recentOutput"/> against
    /// every mode declared in <paramref name="modes"/> and return the
    /// matching mode plus an optional nesting level.
    /// </summary>
    public static ModeMatch Detect(IReadOnlyList<ModeSpec>? modes, string recentOutput)
    {
        if (modes == null || modes.Count == 0)
            return new ModeMatch(Name: null, Level: null);

        // Auto-enter modes are scanned first — they're the ones that
        // can "trap" the user (debugger entered via breakpoint(),
        // pry break reached, etc.). Default mode is the fallback.
        foreach (var mode in modes)
        {
            if (!mode.AutoEnter) continue;

            // Prefer the dedicated `detect` regex (the "is the user
            // now in this mode?" predicate) and fall back to
            // `primary` (the prompt regex itself) for adapters that
            // don't bother to separate them.
            var pattern = mode.Detect ?? mode.Primary;
            if (string.IsNullOrEmpty(pattern)) continue;

            Regex re;
            try { re = new Regex(pattern, RegexOptions.Multiline, MatchTimeout); }
            catch { continue; }

            Match match;
            try { match = re.Match(recentOutput); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!match.Success) continue;

            int? level = null;
            if (mode.Nested && mode.LevelCapture is int group && group < match.Groups.Count)
            {
                if (int.TryParse(match.Groups[group].Value, out var parsed))
                    level = parsed;
            }
            return new ModeMatch(mode.Name, level);
        }

        // Fall through: report the default mode (or the first one).
        var defaultMode = modes.FirstOrDefault(m => m.Default) ?? modes[0];
        return new ModeMatch(defaultMode.Name, Level: null);
    }
}

/// <summary>
/// Result of a <see cref="ModeDetector.Detect"/> call. <c>Name</c>
/// is null when the adapter doesn't declare any modes (the schema
/// allows omitting the block entirely for adapters that have only
/// one mode and don't need tracking).
/// </summary>
public record ModeMatch(string? Name, int? Level);
