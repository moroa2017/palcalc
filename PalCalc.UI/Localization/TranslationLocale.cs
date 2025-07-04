﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PalCalc.UI.Localization
{
    public enum TranslationLocale
    {
        // list of localizations supported by Palworld (at time of writing 06/2025), any key in Pal.LocalizedNames should
        // also appear here
        de, en, es_MX, es, fr, id, it, ja, ko, pl, pt_BR, ru, th, tr, vi, zh_Hans, zh_Hant
    }

    public static class TranslationLocaleExtensions
    {
        // convert enum name to stored name for pals + localization files
        // (note: the locale names are all lower-case when we read these from game files)
        public static string ToFormalName(this TranslationLocale locale) => locale.ToString().Replace('_', '-');
    }
}
