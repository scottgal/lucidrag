using System;
using System.Collections.Generic;
using System.IO;

namespace Mostlylucid.DataSummarizer.Models;

public class ReportOptions
{
    public bool GenerateMarkdown { get; set; } = true;
    public bool UseLlm { get; set; } = true;
    public bool IncludeFocusQuestions { get; set; } = true;
    public List<string> FocusQuestions { get; set; } = new();
}
