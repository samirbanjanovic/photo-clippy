using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Recommendation;

internal sealed partial class McdaRun
{
    private static readonly CriterionInfo[] CriterionInfos =
    [
        new(CriterionIds.Fit, "Fit", "Weighted share of your frames whose 35mm-eq focal length the lens covers (zoom range with soft edges, or a prime's natural framing band)."),
        new(CriterionIds.LowLight, "Low light", "Stops gained on frames shot at your lens' maximum aperture, counted 1.5× when the frame was also light-starved (high ISO, slow shutter or dark scene)."),
        new(CriterionIds.Isolation, "Isolation", "Entrance-pupil (focal ÷ f-number) gain over what you used, weighted toward wide-open frames, isolation genres and subject separation."),
        new(CriterionIds.Reach, "Reach", "Frames pinned at a zoom end-stop or outside every owned lens that the candidate covers by going at least 15% further."),
        new(CriterionIds.Genre, "Genre match", "Overlap of the lens with each genre's preferred focal range (×1.5 when the genre prefers fast glass and the lens is fast enough), weighted by your genre mix."),
        new(CriterionIds.Portability, "Portability", "Lightest supplied weight ÷ this lens' weight."),
        new(CriterionIds.Value, "Value", "Lowest supplied price ÷ this lens' price."),
    ];

    private RecommendationSet Assemble()
    {
        AddRunNotes();
        var k = K;
        var order = Enumerable.Range(0, k)
            .OrderByDescending(i => _eligible[i])
            .ThenByDescending(i => _pointScore[i])
            .ThenBy(i => i)
            .ToArray();
        var winner = order[0];
        var topScore = _pointScore[winner];
        var presetWinners = PresetWinnerIndexes();
        var stats = new Stats[k];
        Parallel.For(0, k, new ParallelOptions { CancellationToken = _ct }, ci => stats[ci] = ComputeStats(ci));
        var ranked = new List<LensRecommendation>(k);
        Stats? winnerStats = null;
        for (var r = 0; r < k; r++)
        {
            var ci = order[r];
            var c = _cands[ci];
            var st = stats[ci];
            if (ci == winner)
            {
                winnerStats = st;
            }

            var contribution = new double[NCrit];
            var criteria = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var j = 0; j < NCrit; j++)
            {
                if (_active[j])
                {
                    var v = _pointNorm[(ci * NCrit) + j];
                    criteria[CritIds[j]] = v;
                    contribution[j] = _weights[j] * v;
                }
            }

            var wins = new List<string>();
            if (_eligible[ci] && ci != winner)
            {
                foreach (var (preset, w) in presetWinners)
                {
                    if (w == ci && preset != _preset)
                    {
                        wins.Add($"Ranks first under the {preset} preset");
                    }
                }

                wins.AddRange(Sensitivity(ci));
            }

            ranked.Add(new LensRecommendation
            {
                Lens = c.Spec,
                Rank = r + 1,
                Score = Math.Clamp(_pointScore[ci], 0, 1),
                ScoreLow = Math.Clamp(_scoreLow[ci], 0, 1),
                ScoreHigh = Math.Clamp(_scoreHigh[ci], 0, 1),
                ProbabilityBest = _probBest[ci],
                Criteria = criteria,
                Metrics = BuildMetrics(ci, st),
                Redundancy = c.Redundancy,
                RedundantWith = c.RedundantWith?.Display,
                AlreadyOwned = c.AlreadyOwned,
                Verdict = Verdict(ci, r + 1, topScore, st),
                Evidence = BuildEvidence(ci, st, contribution),
                TradeOffs = BuildTradeOffs(ci, st),
                WinsInsteadWhen = wins,
            });
        }

        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        var infos = new List<CriterionInfo>();
        for (var j = 0; j < NCrit; j++)
        {
            if (_active[j])
            {
                weights[CritIds[j]] = _weights[j];
                infos.Add(CriterionInfos[j]);
            }
        }

        if (_prefs.TopN > 0 && ranked.Count > _prefs.TopN)
        {
            _notes.Add($"Showing the top {_prefs.TopN.ToString(Optics.Inv)} of {ranked.Count.ToString(Optics.Inv)} candidates.");
            ranked = ranked.GetRange(0, _prefs.TopN);
        }

        var winners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (preset, w) in presetWinners)
        {
            winners[preset] = _cands[w].Spec.Id;
        }

        return new RecommendationSet
        {
            RecommenderId = McdaRecommenderFactory.PluginId,
            Preset = _preset,
            Target = _request.Target,
            Weights = weights,
            Criteria = infos,
            SampleCount = _n,
            CandidateCount = k,
            Ranked = ranked,
            PresetWinners = winners,
            Headline = Headline(winner, order, winnerStats!),
            Notes = _notes,
            Assumptions = BuildAssumptions(),
        };
    }

    private void AddRunNotes()
    {
        if (_dropped > 0)
        {
            _notes.Add($"Ignored {_dropped} sample(s) without a usable 35mm-eq focal length or with zero weight.");
        }

        if (_n == 0)
        {
            _notes.Add("No usable frames — scores reflect only portability/value data, if any.");
        }
        else if (_n < 30)
        {
            _notes.Add($"Only {_n} frames — treat the ranking as indicative.");
        }

        if (_owned.Count == 0)
        {
            _notes.Add("No owned lenses supplied — redundancy and range gaps are not assessed.");
        }
        else if (_ownedFromProfile)
        {
            _notes.Add("Owned lenses were inferred from the shooting profile's lens usage.");
        }

        if (_n > 0 && _active[CLow])
        {
            var known = 0.0;
            for (var i = 0; i < _n; i++)
            {
                if ((_flags[i] & FCeilKnown) != 0)
                {
                    known += _w[i];
                }
            }

            if (known < 0.8 * _totalW)
            {
                _notes.Add($"Only {Optics.Pct(known / _totalW)} of frames record both f-number and the lens' maximum aperture; low light is scored on those.");
            }
        }

        if (_validReplicates > 0)
        {
            _notes.Add($"Uncertainty from {_validReplicates} bootstrap resamples of your frames (seed {_prefs.Seed.ToString(Optics.Inv)}).");
        }
    }

    private List<(string Preset, int Winner)> PresetWinnerIndexes()
    {
        var result = new List<(string, int)>();
        var scores = new double[K];
        foreach (var preset in McdaPresets.All)
        {
            var w = Effective(McdaPresets.Table(preset), out _);
            Score(_pointNorm, w, scores);
            var best = Winner(scores);
            if (best >= 0)
            {
                result.Add((preset, best));
            }
        }

        return result;
    }

    private List<string> Sensitivity(int k)
    {
        var options = new List<(double Change, string Text)>();
        for (var j = 0; j < NCrit; j++)
        {
            var wj = _weights[j];
            if (!_active[j] || wj >= 0.999)
            {
                continue;
            }

            var lo = -wj;
            var hi = double.PositiveInfinity;
            var feasible = true;
            var bk = _cands[k].Multiplier * _pointNorm[(k * NCrit) + j];
            for (var i = 0; i < K && feasible; i++)
            {
                if (i == k || !_eligible[i])
                {
                    continue;
                }

                var a = _pointScore[k] - _pointScore[i];
                var d = bk - (_cands[i].Multiplier * _pointNorm[(i * NCrit) + j]);
                if (Math.Abs(d) < 1e-12)
                {
                    feasible = a >= -1e-12;
                }
                else if (d > 0)
                {
                    lo = Math.Max(lo, -a / d);
                }
                else
                {
                    hi = Math.Min(hi, -a / d);
                }
            }

            if (!feasible || lo > hi - 1e-12)
            {
                continue;
            }

            var wLo = Renormalized(wj, lo);
            var wHi = double.IsPositiveInfinity(hi) ? 1.0 : Renormalized(wj, hi);
            var now = Optics.Num(wj, "0.##");
            var label = CritLabels[j];
            if (lo > 1e-12)
            {
                var x = Math.Ceiling((wLo * 100) - 1e-9) / 100;
                if (x > 0.95)
                {
                    continue;
                }

                var text = wHi < 0.995
                    ? $"Ranks first if {label} weight is between {Optics.Num(x, "0.##")} and {Optics.Num(Math.Floor((wHi * 100) + 1e-9) / 100, "0.##")} (now {now})"
                    : $"Ranks first if {label} weight ≥ {Optics.Num(x, "0.##")} (now {now})";
                options.Add((wLo - wj, text));
            }
            else if (hi < -1e-12)
            {
                var x = Math.Floor((wHi * 100) + 1e-9) / 100;
                if (x < 0)
                {
                    continue;
                }

                var text = wLo > 0.005
                    ? $"Ranks first if {label} weight is between {Optics.Num(Math.Ceiling((wLo * 100) - 1e-9) / 100, "0.##")} and {Optics.Num(x, "0.##")} (now {now})"
                    : $"Ranks first if {label} weight ≤ {Optics.Num(x, "0.##")} (now {now})";
                options.Add((wj - wHi, text));
            }
        }

        options.Sort((a, b) => a.Change.CompareTo(b.Change));
        var result = new List<string>();
        foreach (var (_, text) in options)
        {
            if (result.Count == 3)
            {
                break;
            }

            result.Add(text);
        }

        return result;
    }

    private static double Renormalized(double wj, double delta) => (wj + delta) / (1 + delta);

    private string Headline(int winner, int[] order, Stats st)
    {
        var c = _cands[winner];
        if (!_eligible[winner])
        {
            return "Every candidate matches a lens you already own — nothing new to add.";
        }

        var name = Label(c);
        if (_n == 0)
        {
            return $"{name} ranks first on the supplied weight/price data; no frames to compare against.";
        }

        var fit = _pointRaw[(winner * NData) + CFit];
        var text = $"{name} fits {Optics.Pct(fit)} of how you shoot";
        var strength = StrengthCriterion(winner);
        var lowLight = _active[CLow] && st.StopsGained >= 1.0 / 3 && st.CeilingCoveredShare >= 0.1;
        var where = st.StarvedShare >= 0.3 ? "where you are light-limited" : "where you shoot wide open";
        var clause = strength switch
        {
            CLow when lowLight => $" and adds {Optics.StopsText(st.StopsGained)} {where}",
            CIso when double.IsFinite(st.BlurRatio) && st.BlurRatio >= 1.3 => $" and gives ~{Optics.Num(st.BlurRatio, "0.#")}× more background blur",
            CReach when st.ReachShare >= 0.02 => $" and reaches past your lenses for {Optics.Pct(st.ReachShare)} of frames that hit a limit",
            CGenre when st.TopGenre >= 0 => $" and suits your {_genres[st.TopGenre].Name.ToLowerInvariant()} work",
            CPort when c.Spec.WeightG is { } wg && _staticNorm[winner * 2] >= 0.999 => $" and is the lightest option at {Optics.Num(wg, "0")}g",
            CPort when c.Spec.WeightG is { } wg => $" and weighs {Optics.Num(wg, "0")}g",
            CValue when c.Spec.PriceUsd is { } p && _staticNorm[(winner * 2) + 1] >= 0.999 => $" and is the least expensive option at ${Optics.Num(p, "0")}",
            CValue when c.Spec.PriceUsd is { } p => $" and costs ${Optics.Num(p, "0")}",
            _ when lowLight => $" and adds {Optics.StopsText(st.StopsGained)} {where}",
            _ when st.ReachShare >= 0.05 => $" and reaches past your lenses for {Optics.Pct(st.ReachShare)} of frames that hit a limit",
            _ => string.Empty,
        };
        text += clause;
        if (order.Length > 1 && _eligible[order[1]] && _validReplicates > 0 && _probBest[order[1]] >= 0.25)
        {
            text += $" — but {Label(_cands[order[1]])} wins {Optics.Pct(_probBest[order[1]])} of resamples";
        }

        return text;
    }

    private List<string> BuildAssumptions()
    {
        var a = new List<string>();
        var format = string.IsNullOrWhiteSpace(_request.Target.Format) ? "target body" : _request.Target.Format;
        a.Add(Math.Abs(_crop - 1) < 0.03
            ? $"Crop factor {Optics.Num(_crop, "0.0#")} ({format}): candidate focal lengths are native and equal to 35mm-eq."
            : $"Crop factor {Optics.Num(_crop, "0.0#")} ({format}): candidate focal lengths are native for the target body and multiplied by {Optics.Num(_crop, "0.0#")} to compare with your 35mm-eq frames.");
        a.Add("EXIF shows what your kit allowed, not what you wanted: frames at a lens' maximum aperture or zoom end-stop are read as demand for more speed or range.");
        a.Add($"A frame is 'at the aperture ceiling' when shot within ⅓ stop of the lens' maximum aperture; it is light-starved when also at ISO ≥ {_prefs.HighIsoThreshold.ToString(Optics.Inv)}, slower than 1/focal-eq, or below EV100 6 (counted 1.5×).");
        var half = _sigma * Math.Sqrt(2 * Math.Log(2));
        a.Add($"Primes cover about ±{Optics.Num((Math.Exp(half) - 1) * 100, "0")}% of their focal length (log-σ {Optics.Num(_sigma, "0.0#")}); zooms cover their range with a soft edge (log-σ {Optics.Num(Optics.ZoomSigma, "0.0#")}).");
        a.Add("Isolation is modelled by entrance-pupil diameter (focal ÷ f-number); magnification, autofocus, optical quality and rendering are not assessed.");
        a.Add($"Scores are weighted sums of criteria normalized to the best candidate, multiplied by (1 − {Optics.Num(_settings.RedundancyPenalty, "0.##")} × redundancy) against lenses you own.");
        if (_archetypes)
        {
            a.Add("Archetypes are snapped to common 35mm-eq focal lengths and rounded to sensible native focal lengths; they describe specs, not products.");
        }

        if (Math.Clamp(_prefs.BootstrapReplicates, 0, 100_000) > 0)
        {
            a.Add($"Score ranges are the 5th–95th percentile over {Math.Clamp(_prefs.BootstrapReplicates, 0, 100_000).ToString(Optics.Inv)} Poisson(1) bootstrap resamples of your frames (seed {_prefs.Seed.ToString(Optics.Inv)}).");
        }

        return a;
    }
}
