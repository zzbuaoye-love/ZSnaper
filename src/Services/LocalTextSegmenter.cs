using System.Text;
using System.Text.RegularExpressions;

namespace ZSnaper.Services;

/// <summary>
/// 本地智能文本分段引擎：
/// 区分 UI 短行/标题/菜单与长文本段落，避免传统 OCR 清洗将所有独立行强制合并为一团文字，
/// 并支持自动识别融合成一团的文本按语义标点断句分段。
/// </summary>
public static partial class LocalTextSegmenter
{
    [GeneratedRegex(@"[^\S\r\n]+")]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex(@"(?<=[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF])\s+(?=[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF])")]
    private static partial Regex CjkInnerWhitespaceRegex();

    [GeneratedRegex(@"\s+([，。！？；：、,.!?;:%）》】〕〉」』”’…])")]
    private static partial Regex WhitespaceBeforePunctuationRegex();

    [GeneratedRegex(@"([（《【〔〈「『“‘])\s+")]
    private static partial Regex WhitespaceAfterOpeningPunctuationRegex();

    [GeneratedRegex(@"^(\d+[\.、\)]|[-*•+]\s|\([0-9a-zA-Z一二三四五六七八九十]+\)|[一二三四五六七八九十]+[、\.])")]
    private static partial Regex ListMarkerRegex();

    /// <summary>
    /// 智能分段与排版（保留独立行、短标签与列表，合并自然断行段落）
    /// </summary>
    public static string SmartSegment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] rawLines = normalized.Split('\n');

        // 如果整个文本仅有 1-2 行但长度较长且包含多个句子终止标点，说明已被严重粘连，先进行句子拆分
        if (rawLines.Length <= 2 && text.Length > 60 && CountSentenceTerminators(text) >= 2)
        {
            rawLines = SplitFusedSentences(normalized);
        }

        var resultLines = new List<string>();
        string? pendingParagraph = null;

        foreach (string rawLine in rawLines)
        {
            string line = CleanLine(rawLine);
            if (line.Length == 0)
            {
                if (pendingParagraph is not null)
                {
                    resultLines.Add(pendingParagraph);
                    pendingParagraph = null;
                }
                resultLines.Add(string.Empty);
                continue;
            }

            if (pendingParagraph is null)
            {
                pendingParagraph = line;
                continue;
            }

            // 检查当前行与上一行是否应该合并
            if (ShouldMergeLines(pendingParagraph, line))
            {
                if (ShouldInsertSpace(pendingParagraph[^1], line[0]))
                {
                    pendingParagraph += " " + line;
                }
                else
                {
                    pendingParagraph += line;
                }
            }
            else
            {
                resultLines.Add(pendingParagraph);
                pendingParagraph = line;
            }
        }

        if (pendingParagraph is not null)
        {
            resultLines.Add(pendingParagraph);
        }

        // 去除多余连续空行
        return CollapseExcessiveBlankLines(resultLines);
    }

    /// <summary>
    /// 判定两行是否应合并为同一自然段落
    /// </summary>
    private static bool ShouldMergeLines(string prevLine, string nextLine)
    {
        // 1. 如果上一行是独立标题/菜单/UI 短行（不超过 28 字符，且不以句中逗号结尾），不应合并
        if (prevLine.Length <= 28 && !prevLine.EndsWith('，') && !prevLine.EndsWith(','))
        {
            return false;
        }

        // 2. 如果上一行以句末标点结尾（句号、问号、感叹号、冒号、分号等），绝不合并
        char lastChar = prevLine[^1];
        if (lastChar is '。' or '！' or '？' or '!' or '?' or '；' or ';' or '：' or ':')
        {
            return false;
        }

        // 3. 如果当前行是列表项（1.、-、•、一、等），绝不合并
        if (ListMarkerRegex().IsMatch(nextLine))
        {
            return false;
        }

        // 4. 如果当前行是标题标记或括号标记
        if (nextLine.StartsWith('#') || nextLine.StartsWith('【') || nextLine.StartsWith('['))
        {
            return false;
        }

        // 5. 如果当前行是独立短行（例如按钮标签），也不宜合并
        if (nextLine.Length <= 10 && !nextLine.Contains('。') && !nextLine.Contains('，'))
        {
            return false;
        }

        // 否则属于长文本在排版换行处的自然拆分，进行平滑连接
        return true;
    }

    /// <summary>
    /// 针对已经融合粘连成一整团的长文本，根据中文/英文标点智能断开成合理段落
    /// </summary>
    private static string[] SplitFusedSentences(string fusedText)
    {
        var lines = new List<string>();
        var current = new StringBuilder();

        for (int i = 0; i < fusedText.Length; i++)
        {
            char c = fusedText[i];
            current.Append(c);

            // 中文句末标点：。！？
            if (c is '。' or '！' or '？')
            {
                // 如果后面跟着引号括号，先吃进引号
                while (i + 1 < fusedText.Length && fusedText[i + 1] is '”' or '’' or '）' or '」' or '』')
                {
                    i++;
                    current.Append(fusedText[i]);
                }

                lines.Add(current.ToString().Trim());
                current.Clear();
            }
            // 英文句末标点：. ! ? 且后面紧跟空格和大写字母或换行
            else if (c is '.' or '!' or '?')
            {
                if (i + 1 < fusedText.Length && char.IsWhiteSpace(fusedText[i + 1]))
                {
                    while (i + 1 < fusedText.Length && char.IsWhiteSpace(fusedText[i + 1]))
                    {
                        i++;
                    }
                    lines.Add(current.ToString().Trim());
                    current.Clear();
                }
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString().Trim());
        }

        return lines.Where(l => l.Length > 0).ToArray();
    }

    private static int CountSentenceTerminators(string text)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (c is '。' or '！' or '？' or '!' or '?')
            {
                count++;
            }
        }
        return count;
    }

    private static string CleanLine(string line)
    {
        string normalized = HorizontalWhitespaceRegex().Replace(line.Trim(), " ");
        normalized = CjkInnerWhitespaceRegex().Replace(normalized, string.Empty);
        normalized = WhitespaceBeforePunctuationRegex().Replace(normalized, "$1");
        normalized = WhitespaceAfterOpeningPunctuationRegex().Replace(normalized, "$1");
        return normalized;
    }

    private static bool ShouldInsertSpace(char left, char right)
    {
        if (IsCjk(left) || IsCjk(right))
        {
            return false;
        }

        return !char.IsPunctuation(left) && !char.IsPunctuation(right);
    }

    private static bool IsCjk(char c) =>
        c is (>= '\u3400' and <= '\u4DBF') or
             (>= '\u4E00' and <= '\u9FFF') or
             (>= '\uF900' and <= '\uFAFF');

    private static string CollapseExcessiveBlankLines(List<string> lines)
    {
        var sb = new StringBuilder();
        bool lastWasEmpty = false;

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            bool isEmpty = string.IsNullOrWhiteSpace(line);

            if (isEmpty)
            {
                if (!lastWasEmpty && sb.Length > 0)
                {
                    sb.AppendLine();
                    lastWasEmpty = true;
                }
            }
            else
            {
                sb.AppendLine(line);
                lastWasEmpty = false;
            }
        }

        return sb.ToString().TrimEnd();
    }
}

