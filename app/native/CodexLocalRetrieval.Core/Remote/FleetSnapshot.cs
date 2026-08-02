using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexLocalRetrieval.Core.Remote;

public sealed record FleetSessionRecord(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("pids")] List<int> Pids,
    [property: JsonPropertyName("firstSeenUtc")] string FirstSeenUtc,
    [property: JsonPropertyName("lastSeenUtc")] string LastSeenUtc);

public sealed record FleetState
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "rolling";

    [JsonPropertyName("savedAtUtc")]
    public string SavedAtUtc { get; init; } = "";

    [JsonPropertyName("bootStampUtc")]
    public string BootStampUtc { get; init; } = "";

    [JsonPropertyName("sessions")]
    public List<FleetSessionRecord> Sessions { get; init; } = new();
}

public static class FleetStore
{
    private const string NameRule = "name must match ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$";
    private static readonly TimeSpan DefaultBootTolerance = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public sealed record Options(string? RootDirectory = null, DateTimeOffset? Now = null)
    {
        internal string EffectiveRootDirectory
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(RootDirectory))
                {
                    return RootDirectory!;
                }

                var localApplicationData =
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localApplicationData))
                {
                    localApplicationData = Path.GetTempPath();
                }

                return Path.Combine(localApplicationData, "CodexLocalRetrieval", "fleet");
            }
        }
    }

    public static string CurrentBootStampUtc(DateTimeOffset? now = null)
    {
        var effectiveNow = now ?? DateTimeOffset.UtcNow;
        return effectiveNow.AddMilliseconds(-Environment.TickCount64).ToString("O", CultureInfo.InvariantCulture);
    }

    public static bool SameBoot(string a, string b, TimeSpan? tolerance = null)
    {
        if (!DateTimeOffset.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.None, out var first) ||
            !DateTimeOffset.TryParse(b, CultureInfo.InvariantCulture, DateTimeStyles.None, out var second))
        {
            return false;
        }

        var allowed = tolerance ?? DefaultBootTolerance;
        return (first - second).Duration() <= allowed;
    }

    public static bool TryWriteCurrent(FleetState state, out string detail, Options? options = null)
    {
        detail = "";

        try
        {
            var path = Path.Combine(EffectiveRootDirectory(options), "current.json");
            return TryWriteAtomic(path, state, out detail);
        }
        catch (Exception ex)
        {
            detail = ErrorDetail("could not write current state", ex);
            return false;
        }
    }

    public static bool TryReadCurrent(out FleetState? state, out string detail, Options? options = null)
    {
        state = null;
        detail = "";

        try
        {
            var path = Path.Combine(EffectiveRootDirectory(options), "current.json");
            if (TryReadStateFile(path, out state, out var missing, out detail))
            {
                return true;
            }

            if (missing)
            {
                state = null;
                detail = "";
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            state = null;
            detail = ErrorDetail("could not read current state", ex);
            return false;
        }
    }

    public static bool TrySaveNamed(string name, FleetState state, out string detail, Options? options = null)
    {
        detail = "";
        if (!IsValidName(name))
        {
            detail = NameRule;
            return false;
        }

        try
        {
            var storedState = state with { Name = name };
            var path = Path.Combine(EffectiveRootDirectory(options), "states", name + ".json");
            return TryWriteAtomic(path, storedState, out detail);
        }
        catch (Exception ex)
        {
            detail = ErrorDetail("could not save named state", ex);
            return false;
        }
    }

    public static bool TryListStates(out List<FleetState> states, out string detail, Options? options = null)
    {
        states = new List<FleetState>();
        detail = "";
        var skippedFiles = new List<string>();
        var entries = new List<ListEntry>();

        try
        {
            var directory = Path.Combine(EffectiveRootDirectory(options), "states");
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }

            foreach (var file in files)
            {
                if (!TryReadStateFile(file, out var state, out _, out _))
                {
                    skippedFiles.Add(Path.GetFileName(file));
                    continue;
                }

                if (state is null)
                {
                    skippedFiles.Add(Path.GetFileName(file));
                    continue;
                }

                var hasSavedAt = DateTimeOffset.TryParse(
                    state.SavedAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var savedAt);
                entries.Add(new ListEntry(state, hasSavedAt, savedAt, Path.GetFileName(file)));
            }

            entries.Sort(static (left, right) =>
            {
                if (left.HasSavedAt != right.HasSavedAt)
                {
                    return left.HasSavedAt ? -1 : 1;
                }

                if (left.HasSavedAt)
                {
                    var savedAtComparison = right.SavedAt.CompareTo(left.SavedAt);
                    if (savedAtComparison != 0)
                    {
                        return savedAtComparison;
                    }
                }

                return StringComparer.OrdinalIgnoreCase.Compare(left.FileName, right.FileName);
            });

            foreach (var entry in entries)
            {
                states.Add(entry.State);
            }

            if (skippedFiles.Count > 0)
            {
                detail = "skipped invalid state files: " + string.Join(", ", skippedFiles);
            }

            return true;
        }
        catch (Exception ex)
        {
            states.Clear();
            detail = ErrorDetail("could not list saved states", ex);
            return false;
        }
    }

    public static bool TryReadState(string name, out FleetState? state, out string detail, Options? options = null)
    {
        state = null;
        detail = "";
        if (!IsValidName(name))
        {
            detail = NameRule;
            return false;
        }

        try
        {
            var path = Path.Combine(EffectiveRootDirectory(options), "states", name + ".json");
            if (TryReadStateFile(path, out state, out var missing, out detail))
            {
                return true;
            }

            if (missing)
            {
                state = null;
                detail = "no such state";
            }

            return false;
        }
        catch (Exception ex)
        {
            state = null;
            detail = ErrorDetail("could not read named state", ex);
            return false;
        }
    }

    public static bool TryDeleteState(string name, out string detail, Options? options = null)
    {
        detail = "";
        if (!IsValidName(name))
        {
            detail = NameRule;
            return false;
        }

        try
        {
            var path = Path.Combine(EffectiveRootDirectory(options), "states", name + ".json");
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            detail = ErrorDetail("could not delete named state", ex);
            return false;
        }
    }

    public static bool TryPromoteStaleCurrent(out string? promotedName, out string detail, Options? options = null)
    {
        promotedName = null;
        detail = "";

        try
        {
            var effectiveNow = EffectiveNow(options);
            if (!TryReadCurrent(out var current, out detail, options))
            {
                return false;
            }

            if (current is null ||
                SameBoot(current.BootStampUtc, CurrentBootStampUtc(effectiveNow)))
            {
                return true;
            }

            var promotionTime = effectiveNow;
            if (DateTimeOffset.TryParse(
                    current.SavedAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var savedAt))
            {
                promotionTime = savedAt.ToUniversalTime();
            }

            var name = "pre-reboot-" +
                       promotionTime.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var path = Path.Combine(EffectiveRootDirectory(options), "states", name + ".json");
            if (File.Exists(path))
            {
                return true;
            }

            var promotedState = current with
            {
                Name = name,
                Kind = "boot"
            };

            if (!TryWriteAtomic(path, promotedState, out detail))
            {
                return false;
            }

            promotedName = name;
            return true;
        }
        catch (Exception ex)
        {
            promotedName = null;
            detail = ErrorDetail("could not promote stale current state", ex);
            return false;
        }
    }

    private static string EffectiveRootDirectory(Options? options)
    {
        return (options ?? new Options()).EffectiveRootDirectory;
    }

    private static DateTimeOffset EffectiveNow(Options? options)
    {
        return options?.Now ?? DateTimeOffset.UtcNow;
    }

    private static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || !IsAsciiLetterOrDigit(name[0]))
        {
            return false;
        }

        for (var index = 1; index < name.Length; index++)
        {
            var character = name[index];
            if (!IsAsciiLetterOrDigit(character) && character != '.' && character != '_' && character != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetterOrDigit(char character)
    {
        return character is >= 'A' and <= 'Z' ||
               character is >= 'a' and <= 'z' ||
               character is >= '0' and <= '9';
    }

    private static bool TryWriteAtomic(string path, FleetState state, out string detail)
    {
        detail = "";
        var temporaryPath = path + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                throw new IOException("destination has no directory");
            }

            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(state, SerializerOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            detail = ErrorDetail("could not write state file", ex);
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Preserve the original write failure.
            }

            return false;
        }
    }

    private static bool TryReadStateFile(
        string path,
        out FleetState? state,
        out bool missing,
        out string detail)
    {
        state = null;
        missing = false;
        detail = "";

        try
        {
            var json = File.ReadAllText(path);
            state = JsonSerializer.Deserialize<FleetState>(json, SerializerOptions);
            if (state is null)
            {
                detail = "state file contains no state";
                return false;
            }

            return true;
        }
        catch (FileNotFoundException)
        {
            missing = true;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            missing = true;
            return false;
        }
        catch (Exception ex)
        {
            detail = ErrorDetail("could not parse state file", ex);
            return false;
        }
    }

    private static string ErrorDetail(string operation, Exception exception)
    {
        var message = exception.Message;
        return string.IsNullOrWhiteSpace(message)
            ? operation + ": " + exception.GetType().Name
            : operation + ": " + message;
    }

    private sealed record ListEntry(
        FleetState State,
        bool HasSavedAt,
        DateTimeOffset SavedAt,
        string FileName);
}
