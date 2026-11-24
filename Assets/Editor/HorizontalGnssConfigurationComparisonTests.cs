using System;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;

public sealed class HorizontalGnssConfigurationComparisonTests
{
    private const long Ms = 1_000_000;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static string N(float x) => x.ToString("R", Invariant);

    [Test]
    public void ActualDefaultEstimator_ExportsIdenticalSyntheticAndRecordedInputSchedule()
    {
        var output = Environment.GetEnvironmentVariable("DJI_HORIZONTAL_COMPARISON_DIR");
        var input = Environment.GetEnvironmentVariable("DJI_HORIZONTAL_REPLAY_INPUT");
        Assert.That(output, Is.Not.Null.And.Not.Empty);
        Assert.That(File.Exists(input), Is.True);
        Directory.CreateDirectory(output);
        var csv = new StringBuilder("scenario,evaluationNs,sourceNs,baselineNs,fresh,trusted,rawN,rawE,acceptedNs,admitted,reason,state,outputN,outputE,stepM,correctionSpeedMps,tau,deadband\n");
        foreach (var scenario in new[] { "stationary_noise", "slow_005", "slow_010", "move_050", "start_stop", "large_jump", "stale", "quality_hold", "unit_step" })
        {
            var estimator = new HorizontalGnssSmoothingEstimator();
            estimator.Initialize(1000 * Ms);
            long source = 1000 * Ms;
            Vector2 target = Vector2.zero;
            for (int t = 1000; t <= 16000; t += 20)
            {
                float seconds = (t - 1000) / 1000f;
                if ((t - 1000) % 200 == 0 && !(scenario == "stale" && t >= 5000 && t < 5800))
                {
                    source = t * Ms;
                    target = Input(scenario, seconds);
                }
                bool fresh = t * Ms - source <= 300 * Ms;
                bool trusted = !(scenario == "quality_hold" && t >= 5000 && t < 8000);
                var e = trusted ? estimator.Update(t * Ms, fresh, target, source) : estimator.FreezeForGnssQuality(t * Ms, "REPLAY_QUALITY_HOLD");
                Append(csv, scenario, t * Ms, source, 1000 * Ms, fresh, trusted, target, e);
            }
        }
        HorizontalGnssSmoothingEstimator historical = null;
        long baseline = 0;
        int previousSegment = -1;
        var rows = File.ReadAllLines(input);
        Assert.That(rows.Length, Is.GreaterThan(800));
        for (int i = 1; i < rows.Length; i++)
        {
            var r = rows[i].Split(',');
            long b = long.Parse(r[0], Invariant), time = long.Parse(r[1], Invariant), source = long.Parse(r[2], Invariant);
            var target = new Vector2(float.Parse(r[3], Invariant), float.Parse(r[4], Invariant));
            bool fresh = r[5] == "1", trusted = r[6] == "1";
            int segment = int.Parse(r[10], Invariant);
            if (historical == null || b != baseline || segment != previousSegment)
            {
                historical = new HorizontalGnssSmoothingEstimator(); historical.Initialize(b); baseline = b; previousSegment = segment;
                historical.Update(b, true, new Vector2(float.Parse(r[8], Invariant), float.Parse(r[9], Invariant)), long.Parse(r[7], Invariant));
            }
            var e = trusted ? historical.Update(time, fresh, target, source) : historical.FreezeForGnssQuality(time, "REPLAY_QUALITY_HOLD");
            Append(csv, "nov11_recorded", time, source, b, fresh, trusted, target, e);
        }
        File.WriteAllText(Path.Combine(output, "horizontal-response.csv"), csv.ToString());
    }

    private static Vector2 Input(string scenario, float seconds)
    {
        float moving = Mathf.Clamp(seconds - 1, 0, 8);
        return scenario switch
        {
            "stationary_noise" => seconds == 0 ? Vector2.zero : new Vector2(.055f * Mathf.Sin(seconds * .9f), .018f * Mathf.Cos(seconds * 1.3f)),
            "slow_005" => Vector2.right * (.05f * moving),
            "slow_010" => Vector2.right * (.10f * moving),
            "move_050" => Vector2.right * (.50f * moving),
            "start_stop" => Vector2.right * (.5f * Mathf.Clamp(seconds - 2, 0, 4) + .5f * Mathf.Clamp(seconds - 7, 0, 1)),
            "large_jump" => seconds < 3 ? Vector2.zero : Vector2.right * 10,
            "unit_step" => seconds < 1 ? Vector2.zero : Vector2.right,
            _ => Vector2.right * (.5f * moving)
        };
    }

    private static void Append(StringBuilder csv, string scenario, long time, long source, long baseline,
        bool fresh, bool trusted, Vector2 raw, HorizontalGnssSmoothingEstimate e)
    {
        Assert.That(float.IsFinite(e.smoothedNavigationDelta.x) && float.IsFinite(e.smoothedNavigationDelta.y), Is.True);
        Assert.That(e.correctionSpeedMetersPerSecond, Is.LessThanOrEqualTo(2.5001f));
        csv.Append(scenario).Append(',').Append(time).Append(',').Append(source).Append(',').Append(baseline).Append(',')
            .Append(fresh ? 1 : 0).Append(',').Append(trusted ? 1 : 0).Append(',').Append(N(raw.x)).Append(',').Append(N(raw.y)).Append(',')
            .Append(e.acceptedGnssTargetTimestampNs).Append(',').Append(e.targetAccepted ? 1 : 0).Append(',').Append(e.targetRejectedReason).Append(',')
            .Append(e.state).Append(',').Append(N(e.smoothedNavigationDelta.x)).Append(',').Append(N(e.smoothedNavigationDelta.y)).Append(',')
            .Append(N(e.appliedCorrection.magnitude)).Append(',').Append(N(e.correctionSpeedMetersPerSecond)).Append(',')
            .Append(N(e.tauSeconds)).Append(',').Append(N(HorizontalGnssSmoothingEstimator.ProvisionalStationaryNoiseEnvelopeMeters)).Append('\n');
    }
}
