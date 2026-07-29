using System.Text.RegularExpressions;

namespace FlowType.Engine;

/// <summary>
/// British vs American English.
///
/// Whisper is trained mostly on American-spelled text, so a British speaker
/// saying "colour" still gets "color" typed out. Two levers fix that, and
/// FlowType pulls both:
///
/// 1. **Recognition bias** — the variant's sample text goes into whisper's
///    initial prompt, which conditions the decoder toward that spelling
///    convention (and, for en-GB, British vocabulary).
/// 2. **Spelling conversion** — a deterministic post-pass over the transcript,
///    covering the regular morphological families (-our/-or, -ise/-ize,
///    -re/-er, -ce/-se, -ogue/-og, doubled-l inflections) plus an explicit
///    list of irregulars. Case and capitalisation are preserved.
///
/// Accent handling itself is the acoustic model's job, not this class's:
/// strong regional accents are better served by a larger model, which the UI
/// says out loud when a variant is selected.
/// </summary>
public static class EnglishVariant
{
    public const string Off = "off";
    public const string Us = "us";
    public const string Uk = "uk";

    /// <summary>Prompt text that conditions whisper toward the chosen convention.</summary>
    public static string? RecognitionPrompt(string variant) => variant switch
    {
        Uk => "The following is British English, using British spelling and vocabulary: "
            + "colour, favourite, realise, organised, centre, theatre, defence, licence, "
            + "travelled, apologise, programme, whilst, learnt, maths, aluminium.",
        Us => "The following is American English, using American spelling: "
            + "color, favorite, realize, organized, center, theater, defense, license, "
            + "traveled, apologize, program, while, learned, math, aluminum.",
        _ => null,
    };

    /// <summary>Rewrite <paramref name="text"/> into the requested convention.</summary>
    public static string Convert(string text, string variant)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return variant switch
        {
            Uk => Apply(text, toBritish: true),
            Us => Apply(text, toBritish: false),
            _ => text,
        };
    }

    // ----- Irregulars: pairs that no rule generalizes (US, UK) -----

    private static readonly (string Us, string Uk)[] WordPairs =
    {
        ("airplane", "aeroplane"), ("aluminum", "aluminium"), ("analyze", "analyse"),
        ("anesthesia", "anaesthesia"), ("apologize", "apologise"), ("artifact", "artefact"),
        ("catalog", "catalogue"), ("check", "cheque"), ("cozy", "cosy"),
        ("defense", "defence"), ("diarrhea", "diarrhoea"), ("draft", "draught"),
        ("encyclopedia", "encyclopaedia"), ("fetus", "foetus"), ("gray", "grey"),
        ("jewelry", "jewellery"), ("license", "licence"), ("maneuver", "manoeuvre"),
        ("math", "maths"), ("mold", "mould"), ("mustache", "moustache"),
        ("offense", "offence"), ("pajamas", "pyjamas"), ("paralyze", "paralyse"),
        ("plow", "plough"), ("practice", "practise"), ("pretense", "pretence"),
        ("program", "programme"), ("skeptic", "sceptic"), ("skeptical", "sceptical"),
        ("smolder", "smoulder"), ("specialty", "speciality"), ("spelled", "spelt"),
        ("story", "storey"), ("sulfur", "sulphur"), ("tire", "tyre"),
        ("whiskey", "whisky"), ("yogurt", "yoghurt"),
        // Past tenses British English keeps irregular.
        ("burned", "burnt"), ("dreamed", "dreamt"), ("leaned", "leant"),
        ("leaped", "leapt"), ("learned", "learnt"), ("spoiled", "spoilt"),
    };

    /// <summary>Suffix families, expressed as (US ending, UK ending) + the stems they apply to.</summary>
    private static readonly (string UsSuffix, string UkSuffix, string[] Stems)[] SuffixFamilies =
    {
        // -or / -our
        ("or", "our", new[]
        {
            "col", "fav", "flav", "hum", "lab", "neighb", "hon", "hark", "rum", "vap",
            "behavi", "endeav", "harb", "arm", "od", "parl", "sav", "splend", "vig",
        }),
        // -er / -re
        ("er", "re", new[]
        {
            "cent", "theat", "met", "lit", "fib", "calib", "somb", "spect", "manoeuv",
        }),
        // -ize / -ise family (verb stems; also covers -ization/-isation via inflection)
        ("ize", "ise", new[]
        {
            "real", "organ", "recogn", "special", "apolog", "critic", "author",
            "custom", "emphas", "final", "general", "initial", "legal", "maxim",
            "minim", "modern", "normal", "optim", "prior", "random", "summar",
            "util", "visual", "categor", "central", "civil", "colon", "commercial",
            "sympath", "capital", "digit", "harmon", "hospital", "human", "ideal",
            "immun", "industrial", "memor", "mobil", "monopol", "neutral", "personal",
            "popular", "rational", "social", "special", "stabil", "standard", "steril",
            "symbol", "synchron", "terror", "urban", "vapor", "verbal", "vital",
        }),
        // -yze / -yse
        ("yze", "yse", new[] { "anal", "catal", "paral", "paraly", "paralys", "dial" }),
    };

    private static readonly Lazy<Dictionary<string, string>> UsToUk = new(() => BuildMap(true));
    private static readonly Lazy<Dictionary<string, string>> UkToUs = new(() => BuildMap(false));

    /// <summary>Expand every pair/family into a flat lookup, inflections included.</summary>
    private static Dictionary<string, string> BuildMap(bool toBritish)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void AddPair(string us, string uk)
        {
            var (from, to) = toBritish ? (us, uk) : (uk, us);
            if (from.Length > 0 && !map.ContainsKey(from)) map[from] = to;
        }

        foreach (var (us, uk) in WordPairs) AddPair(us, uk);

        foreach (var (usSuffix, ukSuffix, stems) in SuffixFamilies)
        {
            foreach (var stem in stems)
            {
                var us = stem + usSuffix;
                var uk = stem + ukSuffix;
                AddPair(us, uk);

                // Inflections. British doubles the l before a vowel suffix
                // (travelled/travelling) and keeps -s- through -isation.
                if (usSuffix == "ize")
                {
                    AddPair(us + "d", uk + "d");
                    AddPair(us + "s", uk + "s");
                    AddPair(stem + "izing", stem + "ising");
                    AddPair(stem + "ization", stem + "isation");
                    AddPair(stem + "izations", stem + "isations");
                    AddPair(stem + "izer", stem + "iser");
                    AddPair(stem + "izers", stem + "isers");
                }
                else if (usSuffix == "yze")
                {
                    AddPair(us + "d", uk + "d");
                    AddPair(us + "s", uk + "s");
                    AddPair(stem + "yzing", stem + "ysing");
                }
                else if (usSuffix == "or")
                {
                    // Only suffixes that keep the u in British English.
                    // Deliberately excluded: -ous, -ary, -ific, -ation
                    // (humorous, honorary, honorific, coloration drop the u in
                    // both dialects, so mapping them would be wrong).
                    foreach (var inflection in new[]
                    {
                        "s", "ed", "ing", "ful", "fully", "less", "ite", "ites",
                        "able", "ably", "ites",
                    })
                    {
                        AddPair(us + inflection, uk + inflection);
                    }
                }
                else if (usSuffix == "er")
                {
                    AddPair(us + "s", uk + "s");
                }
            }
        }

        // Doubled-l inflections (US single l, UK double l).
        foreach (var stem in new[]
        {
            "travel", "cancel", "label", "model", "signal", "total", "marvel", "fuel",
            "level", "counsel", "equal", "quarrel", "rival", "shovel", "channel",
        })
        {
            AddPair(stem + "ed", stem + "led");
            AddPair(stem + "ing", stem + "ling");
            AddPair(stem + "er", stem + "ler");
            AddPair(stem + "ers", stem + "lers");
        }

        // Plurals of the irregular nouns.
        foreach (var (us, uk) in WordPairs)
        {
            if (us.EndsWith('y') && uk.EndsWith('y'))
            {
                AddPair(us[..^1] + "ies", uk[..^1] + "ies");
            }
            else if (us is not ("math" or "maths"))
            {
                AddPair(us + "s", uk + "s");
            }
        }

        return map;
    }

    private static string Apply(string text, bool toBritish)
    {
        var map = toBritish ? UsToUk.Value : UkToUs.Value;

        return Regex.Replace(text, @"\b[\p{L}']+\b", match =>
        {
            if (!map.TryGetValue(match.Value, out var replacement)) return match.Value;
            return MatchCase(match.Value, replacement);
        });
    }

    /// <summary>Carry the original word's casing onto the replacement.</summary>
    private static string MatchCase(string original, string replacement)
    {
        if (original.All(char.IsUpper) && original.Length > 1) return replacement.ToUpperInvariant();
        if (char.IsUpper(original[0]))
        {
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        }
        return replacement;
    }
}
