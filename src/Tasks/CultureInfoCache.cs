﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Tasks
{
    /// <summary>
    /// Provides read-only cached instances of <see cref="CultureInfo"/>.
    /// <remarks>
    /// Original source:
    /// https://raw.githubusercontent.com/aspnet/Localization/dev/src/Microsoft.Framework.Globalization.CultureInfoCache/CultureInfoCache.cs
    /// </remarks>
    /// </summary>
    internal static class CultureInfoCache
    {
        private static readonly HashSet<string> ValidCultureNames;

        static CultureInfoCache()
        {
            ValidCultureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

#if !FEATURE_CULTUREINFO_GETCULTURES
            if (!AssemblyUtilities.CultureInfoHasGetCultures())
            {
                ValidCultureNames = HardcodedCultureNames;
                return;
            }
#endif

            foreach (var cultureName in AssemblyUtilities.GetAllCultures())
            {
                ValidCultureNames.Add(cultureName.Name);
            }
        }

        /// <summary>
        /// Determine if a culture string represents a valid <see cref="CultureInfo"/> instance.
        /// </summary>
        /// <param name="name">The culture name.</param>
        /// <returns>True if the culture is determined to be valid.</returns>
        internal static bool IsValidCultureString(string name)
        {
            return ValidCultureNames.Contains(name);
        }
        
#if !FEATURE_CULTUREINFO_GETCULTURES
        // copied from https://github.com/aspnet/Localization/blob/5e1fb16071affd15f15b9c732833f3ae2ac46e10/src/Microsoft.Framework.Globalization.CultureInfoCache/CultureInfoList.cs
        // removed the empty string from the list
        private static readonly HashSet<string> HardcodedCultureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "af",
            "af-ZA",
            "am",
            "am-ET",
            "ar",
            "ar-AE",
            "ar-BH",
            "ar-DZ",
            "ar-EG",
            "ar-IQ",
            "ar-JO",
            "ar-KW",
            "ar-LB",
            "ar-LY",
            "ar-MA",
            "ar-OM",
            "ar-QA",
            "ar-SA",
            "ar-SY",
            "ar-TN",
            "ar-YE",
            "arn",
            "arn-CL",
            "as",
            "as-IN",
            "az",
            "az-Cyrl",
            "az-Cyrl-AZ",
            "az-Latn",
            "az-Latn-AZ",
            "ba",
            "ba-RU",
            "be",
            "be-BY",
            "bg",
            "bg-BG",
            "bn",
            "bn-BD",
            "bn-IN",
            "bo",
            "bo-CN",
            "br",
            "br-FR",
            "bs",
            "bs-Cyrl",
            "bs-Cyrl-BA",
            "bs-Latn",
            "bs-Latn-BA",
            "ca",
            "ca-ES",
            "ca-ES-valencia",
            "chr",
            "chr-Cher",
            "chr-Cher-US",
            "co",
            "co-FR",
            "cs",
            "cs-CZ",
            "cy",
            "cy-GB",
            "da",
            "da-DK",
            "de",
            "de-AT",
            "de-CH",
            "de-DE",
            "de-LI",
            "de-LU",
            "dsb",
            "dsb-DE",
            "dv",
            "dv-MV",
            "el",
            "el-GR",
            "en",
            "en-029",
            "en-AU",
            "en-BZ",
            "en-CA",
            "en-GB",
            "en-HK",
            "en-IE",
            "en-IN",
            "en-JM",
            "en-MY",
            "en-NZ",
            "en-PH",
            "en-SG",
            "en-TT",
            "en-US",
            "en-ZA",
            "en-ZW",
            "es",
            "es-419",
            "es-AR",
            "es-BO",
            "es-CL",
            "es-CO",
            "es-CR",
            "es-DO",
            "es-EC",
            "es-ES",
            "es-GT",
            "es-HN",
            "es-MX",
            "es-NI",
            "es-PA",
            "es-PE",
            "es-PR",
            "es-PY",
            "es-SV",
            "es-US",
            "es-UY",
            "es-VE",
            "et",
            "et-EE",
            "eu",
            "eu-ES",
            "fa",
            "fa-IR",
            "ff",
            "ff-Latn",
            "ff-Latn-SN",
            "fi",
            "fi-FI",
            "fil",
            "fil-PH",
            "fo",
            "fo-FO",
            "fr",
            "fr-BE",
            "fr-CA",
            "fr-CD",
            "fr-CH",
            "fr-CI",
            "fr-CM",
            "fr-FR",
            "fr-HT",
            "fr-LU",
            "fr-MA",
            "fr-MC",
            "fr-ML",
            "fr-RE",
            "fr-SN",
            "fy",
            "fy-NL",
            "ga",
            "ga-IE",
            "gd",
            "gd-GB",
            "gl",
            "gl-ES",
            "gn",
            "gn-PY",
            "gsw",
            "gsw-FR",
            "gu",
            "gu-IN",
            "ha",
            "ha-Latn",
            "ha-Latn-NG",
            "haw",
            "haw-US",
            "he",
            "he-IL",
            "hi",
            "hi-IN",
            "hr",
            "hr-BA",
            "hr-HR",
            "hsb",
            "hsb-DE",
            "hu",
            "hu-HU",
            "hy",
            "hy-AM",
            "id",
            "id-ID",
            "ig",
            "ig-NG",
            "ii",
            "ii-CN",
            "is",
            "is-IS",
            "it",
            "it-CH",
            "it-IT",
            "iu",
            "iu-Cans",
            "iu-Cans-CA",
            "iu-Latn",
            "iu-Latn-CA",
            "ja",
            "ja-JP",
            "jv",
            "jv-Latn",
            "jv-Latn-ID",
            "ka",
            "ka-GE",
            "kk",
            "kk-KZ",
            "kl",
            "kl-GL",
            "km",
            "km-KH",
            "kn",
            "kn-IN",
            "ko",
            "ko-KR",
            "kok",
            "kok-IN",
            "ku",
            "ku-Arab",
            "ku-Arab-IQ",
            "ky",
            "ky-KG",
            "lb",
            "lb-LU",
            "lo",
            "lo-LA",
            "lt",
            "lt-LT",
            "lv",
            "lv-LV",
            "mg",
            "mg-MG",
            "mi",
            "mi-NZ",
            "mk",
            "mk-MK",
            "ml",
            "ml-IN",
            "mn",
            "mn-Cyrl",
            "mn-MN",
            "mn-Mong",
            "mn-Mong-CN",
            "mn-Mong-MN",
            "moh",
            "moh-CA",
            "mr",
            "mr-IN",
            "ms",
            "ms-BN",
            "ms-MY",
            "mt",
            "mt-MT",
            "my",
            "my-MM",
            "nb",
            "nb-NO",
            "ne",
            "ne-IN",
            "ne-NP",
            "nl",
            "nl-BE",
            "nl-NL",
            "nn",
            "nn-NO",
            "no",
            "nqo",
            "nqo-GN",
            "nso",
            "nso-ZA",
            "oc",
            "oc-FR",
            "om",
            "om-ET",
            "or",
            "or-IN",
            "pa",
            "pa-Arab",
            "pa-Arab-PK",
            "pa-IN",
            "pl",
            "pl-PL",
            "prs",
            "prs-AF",
            "ps",
            "ps-AF",
            "pt",
            "pt-AO",
            "pt-BR",
            "pt-PT",
            "qut",
            "qut-GT",
            "quz",
            "quz-BO",
            "quz-EC",
            "quz-PE",
            "rm",
            "rm-CH",
            "ro",
            "ro-MD",
            "ro-RO",
            "ru",
            "ru-RU",
            "rw",
            "rw-RW",
            "sa",
            "sa-IN",
            "sah",
            "sah-RU",
            "sd",
            "sd-Arab",
            "sd-Arab-PK",
            "se",
            "se-FI",
            "se-NO",
            "se-SE",
            "si",
            "si-LK",
            "sk",
            "sk-SK",
            "sl",
            "sl-SI",
            "sma",
            "sma-NO",
            "sma-SE",
            "smj",
            "smj-NO",
            "smj-SE",
            "smn",
            "smn-FI",
            "sms",
            "sms-FI",
            "sn",
            "sn-Latn",
            "sn-Latn-ZW",
            "so",
            "so-SO",
            "sq",
            "sq-AL",
            "sr",
            "sr-Cyrl",
            "sr-Cyrl-BA",
            "sr-Cyrl-CS",
            "sr-Cyrl-ME",
            "sr-Cyrl-RS",
            "sr-Latn",
            "sr-Latn-BA",
            "sr-Latn-CS",
            "sr-Latn-ME",
            "sr-Latn-RS",
            "st",
            "st-ZA",
            "sv",
            "sv-FI",
            "sv-SE",
            "sw",
            "sw-KE",
            "syr",
            "syr-SY",
            "ta",
            "ta-IN",
            "ta-LK",
            "te",
            "te-IN",
            "tg",
            "tg-Cyrl",
            "tg-Cyrl-TJ",
            "th",
            "th-TH",
            "ti",
            "ti-ER",
            "ti-ET",
            "tk",
            "tk-TM",
            "tn",
            "tn-BW",
            "tn-ZA",
            "tr",
            "tr-TR",
            "ts",
            "ts-ZA",
            "tt",
            "tt-RU",
            "tzm",
            "tzm-Latn",
            "tzm-Latn-DZ",
            "tzm-Tfng",
            "tzm-Tfng-MA",
            "ug",
            "ug-CN",
            "uk",
            "uk-UA",
            "ur",
            "ur-IN",
            "ur-PK",
            "uz",
            "uz-Cyrl",
            "uz-Cyrl-UZ",
            "uz-Latn",
            "uz-Latn-UZ",
            "vi",
            "vi-VN",
            "wo",
            "wo-SN",
            "xh",
            "xh-ZA",
            "yo",
            "yo-NG",
            "zgh",
            "zgh-Tfng",
            "zgh-Tfng-MA",
            "zh",
            "zh-CN",
            "zh-Hans",
            "zh-Hant",
            "zh-HK",
            "zh-MO",
            "zh-SG",
            "zh-TW",
            "zu",
            "zu-ZA",
            "zh-CHS",
            "zh-CHT"
        };
#endif
    }
}

