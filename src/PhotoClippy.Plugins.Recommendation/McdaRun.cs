using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Recommendation;

/// <summary>State of one recommendation run. Not shared between calls.</summary>
internal sealed partial class McdaRun
{
    private const int CFit = 0;
    private const int CLow = 1;
    private const int CIso = 2;
    private const int CReach = 3;
    private const int CGenre = 4;
    private const int CPort = 5;
    private const int CValue = 6;
    private const int NCrit = 7;
    private const int NData = 5;

    private const int FCeil = 1;
    private const int FStarved = 2;
    private const int FWide = 4;
    private const int FTele = 8;
    private const int FOutside = 16;
    private const int FCeilKnown = 32;

    private static readonly string[] CritIds = [.. McdaPresets.CriterionOrder];
    private static readonly string[] CritLabels = ["fit", "low-light", "isolation", "reach", "genre", "portability", "value"];

    private readonly McdaSettings _settings;
    private readonly RecommendationRequest _request;
    private readonly RecommendationPreferences _prefs;
    private readonly GenreTaxonomy _taxonomy;
    private readonly CancellationToken _ct;
    private readonly double _crop;
    private readonly double _sigma;
    private readonly List<string> _notes = [];
    private readonly List<GenreInfo> _genres = [];
    private readonly Dictionary<string, int> _genreIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Optic> _owned = [];
    private readonly List<Candidate> _cands = [];
    private readonly bool[] _active = new bool[NCrit];
    private double[] _weights = new double[NCrit];
    private string _preset = McdaPresets.Balanced;
    private bool _ownedFromProfile;
    private bool _archetypes;

    private int _n;
    private int _dropped;
    private double _totalW;
    private double[] _f35 = [];
    private double[] _lnF = [];
    private double[] _w = [];
    private double[] _fnum = [];
    private double[] _ceil = [];
    private double[] _pupil = [];
    private double[] _isoMult = [];
    private double[] _gconf = [];
    private int[] _iso = [];
    private int[] _genre = [];
    private int[] _flags = [];

    public McdaRun(McdaSettings settings, RecommendationRequest request, GenreTaxonomy taxonomy, CancellationToken ct)
    {
        _settings = settings;
        _request = request;
        _prefs = request.Preferences ?? new RecommendationPreferences();
        _taxonomy = taxonomy;
        _ct = ct;
        var crop = request.Target.CropFactor;
        _crop = double.IsFinite(crop) && crop > 0.05 ? crop : 1.0;
        _sigma = settings.PrimeSigma;
    }

    private int K => _cands.Count;

    public RecommendationSet Execute()
    {
        BuildGenres();
        BuildOwned();
        PrepareSamples();
        BuildCandidates();
        if (_cands.Count == 0)
        {
            _notes.Add(_archetypes
                ? "No usable frames to derive suggestions from."
                : "No candidate lenses left to rank.");
            return new RecommendationSet
            {
                RecommenderId = McdaRecommenderFactory.PluginId,
                Preset = McdaPresets.Canonical(_prefs.Preset),
                Target = _request.Target,
                SampleCount = _n,
                CandidateCount = 0,
                Notes = _notes,
                Assumptions = BuildAssumptions(),
            };
        }

        ComputeRedundancy();
        DetermineActive();
        BuildWeights();
        BuildBins();
        _ct.ThrowIfCancellationRequested();
        BuildKernels();
        _ct.ThrowIfCancellationRequested();
        PointEstimate();
        RunBootstrap();
        _ct.ThrowIfCancellationRequested();
        return Assemble();
    }

    private void BuildGenres()
    {
        foreach (var g in _taxonomy.Genres)
        {
            if (g.Junk || g.Affinity is not { } a || !(a.FocalMin35 > 0) || !(a.FocalMax35 >= a.FocalMin35) || _genreIndex.ContainsKey(g.Id))
            {
                continue;
            }

            _genreIndex[g.Id] = _genres.Count;
            _genres.Add(new GenreInfo(g.Id, g.Name, a, Math.Log(a.FocalMin35), Math.Log(a.FocalMax35)));
        }
    }

    private static bool ValidSpec(LensSpec s) =>
        double.IsFinite(s.FocalMinMm) && s.FocalMinMm > 0 && double.IsFinite(s.FocalMaxMm) && s.FocalMaxMm >= s.FocalMinMm - 0.5
        && double.IsFinite(s.MaxApertureWide) && s.MaxApertureWide > 0.5 && double.IsFinite(s.MaxApertureTele) && s.MaxApertureTele > 0.5;

    private void BuildOwned()
    {
        foreach (var o in _request.Owned)
        {
            if (o?.Lens is { } spec && ValidSpec(spec))
            {
                _owned.Add(new Optic(spec, _crop, string.IsNullOrWhiteSpace(spec.Name) ? spec.ShortName : spec.Name));
            }
        }

        if (_owned.Count > 0)
        {
            return;
        }

        var body = _request.Target.BodyKey;
        foreach (var u in _request.Profile.Lenses)
        {
            if (u.IsFixed || u.MaxApertureWide is not { } aw)
            {
                continue;
            }

            if (body is not null && u.Bodies.Count > 0 && !u.Bodies.Contains(body, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var min = u.FocalMinMm ?? (u.FocalMin35 / _crop);
            var max = u.FocalMaxMm ?? (u.FocalMax35 / _crop) ?? min;
            if (min is not { } fmin || max is not { } fmax)
            {
                continue;
            }

            var spec = new LensSpec
            {
                Id = u.Key,
                Name = u.Display,
                FocalMinMm = fmin,
                FocalMaxMm = Math.Max(fmin, fmax),
                MaxApertureWide = aw,
                MaxApertureTele = Math.Max(aw, u.MaxApertureTele ?? aw),
                Source = u.SpecSource,
            };
            if (ValidSpec(spec))
            {
                _owned.Add(new Optic(spec, _crop, u.Display));
                _ownedFromProfile = true;
            }
        }
    }

    private void PrepareSamples()
    {
        var src = _request.Samples;
        var count = src.Count;
        _f35 = new double[count];
        _lnF = new double[count];
        _w = new double[count];
        _fnum = new double[count];
        _ceil = new double[count];
        _pupil = new double[count];
        _isoMult = new double[count];
        _gconf = new double[count];
        _iso = new int[count];
        _genre = new int[count];
        _flags = new int[count];
        var highIso = _prefs.HighIsoThreshold;
        var n = 0;
        for (var i = 0; i < count; i++)
        {
            var s = src[i];
            if (s is null || !double.IsFinite(s.Focal35) || s.Focal35 <= 1 || s.Focal35 > 5000 || !double.IsFinite(s.Weight) || s.Weight <= 0)
            {
                _dropped++;
                continue;
            }

            var f = s.Focal35;
            var ln = Math.Log(f);
            var fn = Optics.Valid(s.FNumber, 0.5, 128) ? s.FNumber!.Value : double.NaN;
            var ceil = Optics.Valid(s.LensMaxApertureAtFocal, 0.5, 64) ? s.LensMaxApertureAtFocal!.Value : double.NaN;
            var flags = 0;
            if (!double.IsNaN(fn) && !double.IsNaN(ceil))
            {
                flags |= FCeilKnown;
                if (fn <= ceil * Optics.CeilingFactor)
                {
                    flags |= FCeil;
                    var slow = Optics.Valid(s.ExposureS, 0, 1e5) && s.ExposureS!.Value > 1.0 / f;
                    var dark = Optics.Valid(s.Ev100, -20, 30) && s.Ev100!.Value < 6;
                    if ((s.Iso is { } iso0 && iso0 >= highIso) || slow || dark)
                    {
                        flags |= FStarved;
                    }
                }
            }

            if (s.AtWideEnd)
            {
                flags |= FWide;
            }

            if (s.AtTeleEnd)
            {
                flags |= FTele;
            }

            if (OutsideOwned(ln))
            {
                flags |= FOutside;
            }

            var native = Optics.Valid(s.FocalMm, 0.5, 5000) ? s.FocalMm!.Value : f / _crop;
            var g = -1;
            var conf = 0.0;
            if (!string.IsNullOrWhiteSpace(s.Genre) && _genreIndex.TryGetValue(s.Genre, out var gi))
            {
                g = gi;
                conf = double.IsFinite(s.GenreConfidence) && s.GenreConfidence > 0 ? Math.Min(1, s.GenreConfidence) : 1.0;
            }

            var intent = 0.0;
            if (!double.IsNaN(fn))
            {
                intent = 0.6;
                if (!double.IsNaN(ceil))
                {
                    var down = Optics.Stops(fn, ceil);
                    intent = 1 - (0.7 * Math.Clamp((down - (1.0 / 3)) / (2.0 / 3), 0, 1));
                }

                if (g >= 0 && _genres[g].Affinity.WantsIsolation)
                {
                    intent *= 1 + conf;
                }

                if (s.Separation is { } sep && double.IsFinite(sep))
                {
                    intent *= 0.5 + Math.Clamp(sep, 0, 1);
                }
            }

            _f35[n] = f;
            _lnF[n] = ln;
            _w[n] = s.Weight;
            _fnum[n] = fn;
            _ceil[n] = ceil;
            _pupil[n] = double.IsNaN(fn) ? double.NaN : native / fn;
            _isoMult[n] = intent;
            _iso[n] = s.Iso is { } iso && iso > 0 ? iso : -1;
            _genre[n] = g;
            _gconf[n] = conf;
            _flags[n] = flags;
            _totalW += s.Weight;
            n++;
        }

        _n = n;
    }

    private bool OutsideOwned(double lnF)
    {
        if (_owned.Count == 0)
        {
            return false;
        }

        foreach (var o in _owned)
        {
            if (lnF >= o.LnMin - Optics.Ln105 && lnF <= o.LnMax + Optics.Ln105)
            {
                return false;
            }
        }

        return true;
    }

    private void BuildCandidates()
    {
        var specs = new List<LensSpec>();
        if (_request.Candidates.Count == 0)
        {
            _archetypes = true;
            specs.AddRange(ProposeArchetypes());
            if (specs.Count > 0)
            {
                _notes.Add("No candidates supplied — these are spec archetypes derived from your focal peaks and gaps, not specific products. "
                    + "Compare real lenses by supplying candidates such as \"24-70/2.8, 35/1.4 410g $1100\".");
            }
        }
        else
        {
            var overBudget = new List<string>();
            var overWeight = new List<string>();
            var invalid = new List<string>();
            foreach (var spec in _request.Candidates)
            {
                if (spec is null || !ValidSpec(spec))
                {
                    invalid.Add(spec?.Id ?? "(null)");
                    continue;
                }

                if (_prefs.BudgetUsd is { } budget && spec.PriceUsd is { } price && price > budget)
                {
                    overBudget.Add(string.Create(Optics.Inv, $"{spec.ShortName} (${price:0})"));
                    continue;
                }

                if (_prefs.MaxWeightG is { } maxW && spec.WeightG is { } wg && wg > maxW)
                {
                    overWeight.Add(string.Create(Optics.Inv, $"{spec.ShortName} ({wg:0}g)"));
                    continue;
                }

                specs.Add(spec);
            }

            if (invalid.Count > 0)
            {
                _notes.Add("Ignored candidates with an implausible focal range or aperture: " + string.Join(", ", invalid) + ".");
            }

            if (overBudget.Count > 0)
            {
                _notes.Add(string.Create(Optics.Inv, $"Excluded over your ${_prefs.BudgetUsd:0} budget: {string.Join(", ", overBudget)}."));
            }

            if (overWeight.Count > 0)
            {
                _notes.Add(string.Create(Optics.Inv, $"Excluded over your {_prefs.MaxWeightG:0}g weight limit: {string.Join(", ", overWeight)}."));
            }

            if (_prefs.BudgetUsd is not null)
            {
                var unpriced = specs.Where(s => s.PriceUsd is null).Select(s => s.ShortName).ToList();
                if (unpriced.Count > 0)
                {
                    _notes.Add("Budget not applied to " + string.Join(", ", unpriced) + " — no price supplied.");
                }
            }

            if (_prefs.MaxWeightG is not null)
            {
                var unweighed = specs.Where(s => s.WeightG is null).Select(s => s.ShortName).ToList();
                if (unweighed.Count > 0)
                {
                    _notes.Add("Weight limit not applied to " + string.Join(", ", unweighed) + " — no weight supplied.");
                }
            }
        }

        foreach (var spec in specs)
        {
            var optic = new Optic(spec, _crop, spec.ShortName);
            var c = new Candidate { Optic = optic, Index = _cands.Count };
            foreach (var o in _owned)
            {
                if (SameLens(optic, o))
                {
                    c.OwnedMatch = o;
                    break;
                }
            }

            if (c.OwnedMatch is { } m)
            {
                _notes.Add($"{spec.ShortName} matches a lens you already own ({m.Display}); it is ranked last.");
            }

            _cands.Add(c);
        }
    }

    private static bool SameLens(Optic a, Optic b) =>
        Math.Abs(a.LnMin - b.LnMin) <= Optics.Ln105
        && Math.Abs(a.LnMax - b.LnMax) <= Optics.Ln105
        && Math.Abs(Optics.Stops(a.Spec.MaxApertureWide, b.Spec.MaxApertureWide)) <= (1.0 / 3) + 0.02
        && Math.Abs(Optics.Stops(a.Spec.MaxApertureTele, b.Spec.MaxApertureTele)) <= (1.0 / 3) + 0.02;

    private void ComputeRedundancy()
    {
        foreach (var c in _cands)
        {
            var (a1, a2) = c.Optic.Band(_sigma);
            var width = a2 - a1;
            var best = 0.0;
            var overlapR = 0.0;
            Optic? with = null;
            foreach (var o in _owned)
            {
                var (b1, b2) = o.Band(_sigma);
                var ov = Optics.Overlap(a1, a2, b1, b2) / width;
                if (ov <= 0)
                {
                    continue;
                }

                var centre = Math.Exp((Math.Max(a1, b1) + Math.Min(a2, b2)) / 2);
                var stops = Optics.Stops(o.ApertureAtEq(centre), c.Optic.ApertureAtEq(centre));
                var adv = 1 - Math.Exp(-1.6 * Math.Max(0, stops));
                var r = Math.Min(1, ov) * (1 - adv);

                // On equal overlap, name the owned lens it duplicates most (least speed gained), e.g. 50/1.8 over 24-70/4 for a 50/1.2.
                if (ov > c.Overlap + 1e-9 || (ov > c.Overlap - 1e-9 && r > overlapR))
                {
                    c.Overlap = Math.Min(1, ov);
                    c.OverlapWith = o;
                    overlapR = r;
                }
                if (r > best)
                {
                    best = r;
                    with = o;
                }
            }

            c.Redundancy = best;
            c.RedundantWith = with;
            c.Multiplier = 1 - (_settings.RedundancyPenalty * best);
        }
    }

    private void DetermineActive()
    {
        var anyCeil = false;
        var anyAperture = false;
        var anyGenre = false;
        for (var i = 0; i < _n; i++)
        {
            anyCeil |= (_flags[i] & FCeilKnown) != 0;
            anyAperture |= !double.IsNaN(_fnum[i]);
            anyGenre |= _genre[i] >= 0;
        }

        _active[CFit] = true;
        _active[CReach] = true;
        _active[CLow] = anyCeil;
        _active[CIso] = anyAperture;
        _active[CGenre] = anyGenre && _genres.Count > 0;
        _active[CPort] = _cands.TrueForAll(c => c.Spec.WeightG is > 0);
        _active[CValue] = _cands.TrueForAll(c => c.Spec.PriceUsd is > 0);

        if (_n > 0 && !anyCeil)
        {
            _notes.Add("Low light not scored — no frame records both its f-number and the lens' maximum aperture.");
        }

        if (_n > 0 && !anyAperture)
        {
            _notes.Add("Isolation not scored — no f-numbers in the samples.");
        }

        if (_n > 0 && !_active[CGenre])
        {
            _notes.Add("Genre not scored — no genre labels in the samples.");
        }

        if (!_archetypes && !_active[CPort])
        {
            var missing = _cands.Where(c => c.Spec.WeightG is not > 0).Select(c => c.Spec.Id);
            _notes.Add("Portability not scored — weight not supplied for all candidates. Add e.g. \"35/1.2 1060g\". Missing for: " + string.Join(", ", missing) + ".");
        }

        if (!_archetypes && !_active[CValue])
        {
            var missing = _cands.Where(c => c.Spec.PriceUsd is not > 0).Select(c => c.Spec.Id);
            _notes.Add("Value not scored — price not supplied for all candidates. Add e.g. \"35/1.2 $2800\". Missing for: " + string.Join(", ", missing) + ".");
        }
    }

    private void BuildWeights()
    {
        if (!McdaPresets.IsKnown(_prefs.Preset))
        {
            _notes.Add($"Unknown preset '{_prefs.Preset}' — using balanced.");
        }

        _preset = McdaPresets.Canonical(_prefs.Preset);
        var table = McdaPresets.Table(_preset);
        var unknown = new List<string>();
        foreach (var (key, value) in _prefs.Weights)
        {
            var idx = Array.FindIndex(CritIds, id => string.Equals(id, key?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (idx < 0 || !double.IsFinite(value) || value < 0)
            {
                unknown.Add(key ?? "(null)");
                continue;
            }

            table[idx] = value;
        }

        if (unknown.Count > 0)
        {
            _notes.Add("Ignored weight overrides: " + string.Join(", ", unknown) + " (known: " + string.Join(", ", CritIds) + "; weights must be ≥ 0).");
        }

        _weights = Effective(table, out var fallback);
        if (fallback)
        {
            _notes.Add("All active criteria had zero weight — using equal weights.");
        }
    }

    private double[] Effective(double[] table, out bool fallback)
    {
        var w = new double[NCrit];
        var sum = 0.0;
        var activeCount = 0;
        for (var j = 0; j < NCrit; j++)
        {
            if (_active[j])
            {
                w[j] = table[j];
                sum += table[j];
                activeCount++;
            }
        }

        fallback = sum <= 1e-12;
        for (var j = 0; j < NCrit; j++)
        {
            if (_active[j])
            {
                w[j] = fallback ? 1.0 / activeCount : w[j] / sum;
            }
        }

        return w;
    }
}
