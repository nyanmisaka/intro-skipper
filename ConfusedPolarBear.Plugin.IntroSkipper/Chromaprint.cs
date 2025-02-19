using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Wrapper for libchromaprint.
/// </summary>
public static class Chromaprint
{
    /// <summary>
    /// Gets or sets the logger.
    /// </summary>
    public static ILogger? Logger { get; set; }

    private static Dictionary<string, string> ChromaprintLogs { get; set; } = new();

    /// <summary>
    /// Check that the installed version of ffmpeg supports chromaprint.
    /// </summary>
    /// <returns>true if a compatible version of ffmpeg is installed, false on any error.</returns>
    public static bool CheckFFmpegVersion()
    {
        try
        {
            // Log the output of "ffmpeg -version".
            ChromaprintLogs["version"] = Encoding.UTF8.GetString(GetOutput("-version", 2000));
            Logger?.LogDebug("ffmpeg version information: {Version}", ChromaprintLogs["version"]);

            // First, validate that the installed version of ffmpeg supports chromaprint at all.
            var muxers = Encoding.UTF8.GetString(GetOutput("-muxers", 2000));
            ChromaprintLogs["muxer list"] = muxers;
            Logger?.LogTrace("ffmpeg muxers: {Muxers}", muxers);

            if (!muxers.Contains("chromaprint", StringComparison.OrdinalIgnoreCase))
            {
                ChromaprintLogs["error"] = "muxer_not_supported";
                Logger?.LogError("The installed version of ffmpeg does not support chromaprint");
                return false;
            }

            // Second, validate that ffmpeg understands the "-fp_format raw" option.
            var muxerHelp = Encoding.UTF8.GetString(GetOutput("-h muxer=chromaprint", 2000));
            ChromaprintLogs["muxer options"] = muxerHelp;
            Logger?.LogTrace("ffmpeg chromaprint help: {MuxerHelp}", muxerHelp);

            if (!muxerHelp.Contains("-fp_format", StringComparison.OrdinalIgnoreCase))
            {
                ChromaprintLogs["error"] = "fp_format_not_supported";
                Logger?.LogError("The installed version of ffmpeg does not support the -fp_format flag");
                return false;
            }
            else if (!muxerHelp.Contains("binary raw fingerprint", StringComparison.OrdinalIgnoreCase))
            {
                ChromaprintLogs["error"] = "fp_format_raw_not_supported";
                Logger?.LogError("The installed version of ffmpeg does not support raw binary fingerprints");
                return false;
            }

            Logger?.LogDebug("Installed version of ffmpeg meets fingerprinting requirements");
            ChromaprintLogs["error"] = "okay";
            return true;
        }
        catch
        {
            ChromaprintLogs["error"] = "unknown_error";
            return false;
        }
    }

    /// <summary>
    /// Fingerprint a queued episode.
    /// </summary>
    /// <param name="episode">Queued episode to fingerprint.</param>
    /// <returns>Numerical fingerprint points.</returns>
    public static uint[] Fingerprint(QueuedEpisode episode)
    {
        // Try to load this episode from cache before running ffmpeg.
        if (LoadCachedFingerprint(episode, out uint[] cachedFingerprint))
        {
            Logger?.LogTrace("Fingerprint cache hit on {File}", episode.Path);
            return cachedFingerprint;
        }

        Logger?.LogDebug(
            "Fingerprinting {Duration} seconds from \"{File}\" (length {Length}, id {Id})",
            episode.FingerprintDuration,
            episode.Path,
            episode.Path.Length,
            episode.EpisodeId);

        var args = string.Format(
            CultureInfo.InvariantCulture,
            "-i \"{0}\" -to {1} -ac 2 -f chromaprint -fp_format raw -",
            episode.Path,
            episode.FingerprintDuration);

        // Returns all fingerprint points as raw 32 bit unsigned integers (little endian).
        var rawPoints = GetOutput(args);
        if (rawPoints.Length == 0 || rawPoints.Length % 4 != 0)
        {
            Logger?.LogWarning("Chromaprint returned {Count} points for \"{Path}\"", rawPoints.Length, episode.Path);
            throw new FingerprintException("chromaprint output for \"" + episode.Path + "\" was malformed");
        }

        var results = new List<uint>();
        for (var i = 0; i < rawPoints.Length; i += 4)
        {
            var rawPoint = rawPoints.Slice(i, 4);
            results.Add(BitConverter.ToUInt32(rawPoint));
        }

        // Try to cache this fingerprint.
        CacheFingerprint(episode, results);

        return results.ToArray();
    }

    /// <summary>
    /// Transforms a Chromaprint into an inverted index of fingerprint points to the last index it appeared at.
    /// </summary>
    /// <param name="fingerprint">Chromaprint fingerprint.</param>
    /// <returns>Inverted index.</returns>
    public static Dictionary<uint, int> CreateInvertedIndex(uint[] fingerprint)
    {
        var invIndex = new Dictionary<uint, int>();

        for (int i = 0; i < fingerprint.Length; i++)
        {
            // Get the current point.
            var point = fingerprint[i];

            // Append the current sample's timecode to the collection for this point.
            invIndex[point] = i;
        }

        return invIndex;
    }

    /// <summary>
    /// Runs ffmpeg and returns standard output.
    /// </summary>
    /// <param name="args">Arguments to pass to ffmpeg.</param>
    /// <param name="timeout">Timeout (in seconds) to wait for ffmpeg to exit.</param>
    private static ReadOnlySpan<byte> GetOutput(string args, int timeout = 60 * 1000)
    {
        var ffmpegPath = Plugin.Instance?.FFmpegPath ?? "ffmpeg";

        // Prepend some flags to prevent FFmpeg from logging it's banner and progress information
        // for each file that is fingerprinted.
        var info = new ProcessStartInfo(ffmpegPath, args.Insert(0, "-hide_banner -loglevel warning "))
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
            ErrorDialog = false,

            // We only consume standardOutput.
            RedirectStandardOutput = true,
            RedirectStandardError = false
        };

        var ffmpeg = new Process
        {
            StartInfo = info
        };

        Logger?.LogDebug("Starting ffmpeg with the following arguments: {Arguments}", ffmpeg.StartInfo.Arguments);
        ffmpeg.Start();

        using (MemoryStream ms = new MemoryStream())
        {
            var buf = new byte[4096];
            var bytesRead = 0;

            do
            {
                bytesRead = ffmpeg.StandardOutput.BaseStream.Read(buf, 0, buf.Length);
                ms.Write(buf, 0, bytesRead);
            }
            while (bytesRead > 0);

            ffmpeg.WaitForExit(timeout);

            return ms.ToArray().AsSpan();
        }
    }

    /// <summary>
    /// Tries to load an episode's fingerprint from cache. If caching is not enabled, calling this function is a no-op.
    /// </summary>
    /// <param name="episode">Episode to try to load from cache.</param>
    /// <param name="fingerprint">Array to store the fingerprint in.</param>
    /// <returns>true if the episode was successfully loaded from cache, false on any other error.</returns>
    private static bool LoadCachedFingerprint(QueuedEpisode episode, out uint[] fingerprint)
    {
        fingerprint = Array.Empty<uint>();

        // If fingerprint caching isn't enabled, don't try to load anything.
        if (!(Plugin.Instance?.Configuration.CacheFingerprints ?? false))
        {
            return false;
        }

        var path = GetFingerprintCachePath(episode);

        // If this episode isn't cached, bail out.
        if (!File.Exists(path))
        {
            return false;
        }

        // TODO: make async
        var raw = File.ReadAllLines(path, Encoding.UTF8);
        var result = new List<uint>();

        // Read each stringified uint.
        result.EnsureCapacity(raw.Length);

        try
        {
            foreach (var rawNumber in raw)
            {
                result.Add(Convert.ToUInt32(rawNumber, CultureInfo.InvariantCulture));
            }
        }
        catch (FormatException)
        {
            // Occurs when the cached fingerprint is corrupt.
            Logger?.LogDebug(
                "Cached fingerprint for {Path} ({Id}) is corrupt, ignoring cache",
                episode.Path,
                episode.EpisodeId);

            return false;
        }

        fingerprint = result.ToArray();
        return true;
    }

    /// <summary>
    /// Cache an episode's fingerprint to disk. If caching is not enabled, calling this function is a no-op.
    /// </summary>
    /// <param name="episode">Episode to store in cache.</param>
    /// <param name="fingerprint">Fingerprint of the episode to store.</param>
    private static void CacheFingerprint(QueuedEpisode episode, List<uint> fingerprint)
    {
        // Bail out if caching isn't enabled.
        if (!(Plugin.Instance?.Configuration.CacheFingerprints ?? false))
        {
            return;
        }

        // Stringify each data point.
        var lines = new List<string>();
        foreach (var number in fingerprint)
        {
            lines.Add(number.ToString(CultureInfo.InvariantCulture));
        }

        // Cache the episode.
        File.WriteAllLinesAsync(GetFingerprintCachePath(episode), lines, Encoding.UTF8).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines the path an episode should be cached at.
    /// </summary>
    /// <param name="episode">Episode.</param>
    private static string GetFingerprintCachePath(QueuedEpisode episode)
    {
        return Path.Join(Plugin.Instance!.FingerprintCachePath, episode.EpisodeId.ToString("N"));
    }

    /// <summary>
    /// Gets Chromaprint debugging logs.
    /// </summary>
    /// <returns>Markdown formatted logs.</returns>
    public static string GetChromaprintLogs()
    {
        var logs = new StringBuilder(1024);

        // Print the Chromaprint detection status at the top.
        // Format: "* Chromaprint: `error`"
        logs.Append("* Chromaprint: `");
        logs.Append(ChromaprintLogs["error"]);
        logs.Append("`\n\n"); // Use two newlines to separate the bulleted list from the logs

        // Print all remaining logs
        foreach (var kvp in ChromaprintLogs)
        {
            var name = kvp.Key;
            var contents = kvp.Value;

            if (string.Equals(name, "error", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            /* Format:
             * FFmpeg NAME:
             * ```
             * LOGS
             * ```
             */
            logs.Append("FFmpeg ");
            logs.Append(name);
            logs.Append(":\n```\n");
            logs.Append(contents);

            // ensure the closing triple backtick is on a separate line
            if (!contents.EndsWith('\n'))
            {
                logs.Append('\n');
            }

            logs.Append("```\n\n");
        }

        return logs.ToString();
    }
}
