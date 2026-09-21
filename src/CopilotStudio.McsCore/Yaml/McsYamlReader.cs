// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Globalization;
using System.Text;

namespace Microsoft.CopilotStudio.McsCore.Yaml;

/// <summary>Reads the YAML produced for agent metadata sidecars and edited by hand in agent workspaces.</summary>
internal static class McsYamlReader
{
    public static Dictionary<string, object?> Parse(string text) => ParseDocument(text).ToDictionary();

    public static McsYamlDocument ParseDocument(string text)
    {
        if (text == null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        return new Parser(text).ParseDocument();
    }

    private sealed class Parser
    {
        private const int MaximumDepth = 128;
        private const int MaximumAliasCount = 2048;
        private const int MaximumExpandedNodes = 50000;
        private const string StandardTagPrefix = "tag:yaml.org,2002:";

        private readonly string _text;
        private readonly Dictionary<string, AnchorInfo> _anchors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _tagHandles = new(StringComparer.Ordinal)
        {
            ["!"] = "!",
            ["!!"] = StandardTagPrefix,
        };
        private int _index;
        private int _line = 1;
        private int _column = 1;
        private int _depth;
        private int _flowParentIndent = -1;
        private int _aliasCount;
        private int _expandedNodes;

        public Parser(string text)
        {
            _text = text;
            if (_text.Length > 0 && _text[0] == '\uFEFF')
            {
                _index = 1;
            }
        }

        public McsYamlDocument ParseDocument()
        {
            SkipDirectives();
            SkipBlankSpace();

            if (IsDocumentMarker("---"))
            {
                ConsumeCount(3);
                SkipBlankSpace();
            }

            if (AtEnd || IsDocumentMarker("..."))
            {
                ConsumeTrailingDocument();
                return new McsYamlDocument(McsYamlNode.ForScalar(null, Position, Position), isEmpty: true);
            }

            var root = ParseNode(-1, allowIndentlessSequence: false);
            SkipBlankSpace();
            ConsumeTrailingDocument();
            return new McsYamlDocument(root, isEmpty: false);
        }

        private void ConsumeTrailingDocument()
        {
            if (IsDocumentMarker("..."))
            {
                ConsumeCount(3);
                SkipBlankSpace();
            }

            if (!AtEnd)
            {
                throw Fail(IsDocumentMarker("---")
                    ? "Multiple YAML documents are not supported."
                    : $"Unexpected content '{DescribeCurrentLine()}'.");
            }
        }

        private void SkipDirectives()
        {
            while (true)
            {
                SkipBlankSpace();
                if (AtEnd || Current != '%')
                {
                    return;
                }

                var start = _index;
                SkipToLineEnd();
                var directive = _text.Substring(start, _index - start).Trim();

                if (directive.StartsWith("%YAML", StringComparison.Ordinal))
                {
                    var version = directive.Substring(5).Trim();
                    if (!version.StartsWith("1.", StringComparison.Ordinal))
                    {
                        throw Fail($"YAML version '{version}' is not supported.");
                    }
                }
                else if (directive.StartsWith("%TAG", StringComparison.Ordinal))
                {
                    var parts = directive.Substring(4).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        _tagHandles[parts[0]] = parts[1];
                    }
                }
            }
        }

        private McsYamlNode ParseNode(int parentIndent, bool allowIndentlessSequence) => ParseNode(parentIndent, allowIndentlessSequence, Position);

        private McsYamlNode ParseNode(int parentIndent, bool allowIndentlessSequence, McsYamlPosition absentPosition)
        {
            SkipBlankSpace();
            if (AtEnd)
            {
                return McsYamlNode.ForScalar(null, absentPosition, absentPosition);
            }

            var indent = _column - 1;
            if (indent < parentIndent || (indent == parentIndent && !(allowIndentlessSequence && IsSequenceEntry())))
            {
                return McsYamlNode.ForScalar(null, absentPosition, absentPosition);
            }

            return ParseNodeAtCursor(indent, parentIndent, allowBlockMapping: true);
        }

        private McsYamlNode ParseNodeAtCursor(int nodeIndent, int parentIndent, bool allowBlockMapping) => ParseNodeAtCursor(nodeIndent, parentIndent, allowBlockMapping, allowIndentlessSequence: false);

        private McsYamlNode ParseNodeAtCursor(int nodeIndent, int parentIndent, bool allowBlockMapping, bool allowIndentlessSequence)
        {
            if (_depth++ > MaximumDepth)
            {
                throw Fail("The document is nested too deeply.");
            }

            try
            {
                var start = Position;
                string? anchor = null;
                string? tag = null;
                var crossedLineBreak = false;

                while (true)
                {
                    var properties = ReadNodeProperties(allowLineBreak: true);

                    if (properties.Alias != null)
                    {
                        var resolved = ResolveAlias(properties.Alias, start, Position);
                        if (allowBlockMapping && resolved.Kind == McsYamlNodeKind.Scalar && IsMappingKeySeparatorAhead())
                        {
                            return ParseBlockMappingFromKey(nodeIndent, resolved);
                        }

                        return resolved;
                    }

                    if (anchor != null && properties.Anchor != null)
                    {
                        throw Fail("A node cannot have more than one anchor.");
                    }

                    if (tag != null && properties.Tag != null)
                    {
                        throw Fail("A node cannot have more than one tag.");
                    }

                    anchor ??= properties.Anchor;
                    tag ??= properties.Tag;

                    if (!properties.CrossedLineBreak)
                    {
                        break;
                    }

                    crossedLineBreak = true;
                    var indent = _column - 1;
                    if (AtEnd || IsDocumentMarker("---") || IsDocumentMarker("...")
                        || indent < parentIndent
                        || (indent == parentIndent && !(allowIndentlessSequence && IsSequenceEntry())))
                    {
                        var absent = McsYamlNode.ForScalar(null, start, start);
                        if (anchor != null)
                        {
                            RegisterAnchor(anchor, absent);
                        }

                        return absent;
                    }

                    nodeIndent = indent;
                    allowBlockMapping = true;

                    if (Current != '&' && Current != '*' && Current != '!')
                    {
                        break;
                    }
                }

                var node = ParseNodeContent(nodeIndent, parentIndent, allowBlockMapping, tag, start, anchor != null && !crossedLineBreak ? anchor : null, tag != null && !crossedLineBreak ? tag : null, out var implicitBlockMapping);
                if (anchor != null && !(implicitBlockMapping && !crossedLineBreak))
                {
                    RegisterAnchor(anchor, node);
                }

                return node;
            }
            finally
            {
                _depth--;
            }
        }

        private void RegisterAnchor(string name, McsYamlNode node) => _anchors[name] = new AnchorInfo(node);

        private McsYamlNode ResolveAlias(string alias, McsYamlPosition start, McsYamlPosition end)
        {
            if (!_anchors.TryGetValue(alias, out var anchor))
            {
                throw Fail($"Alias '*{alias}' does not refer to a known anchor.");
            }

            if (++_aliasCount > MaximumAliasCount)
            {
                throw Fail("The document contains too many aliases.");
            }

            if (anchor.SubtreeCount < 0)
            {
                var (count, depth) = MeasureSubtree(anchor.Node);
                anchor.SubtreeCount = count;
                anchor.SubtreeDepth = depth;
            }

            _expandedNodes += anchor.SubtreeCount;
            if (_expandedNodes > MaximumExpandedNodes)
            {
                throw Fail("The document expands to too many nodes through aliases.");
            }

            if (_depth + anchor.SubtreeDepth > MaximumDepth)
            {
                throw Fail("The document is nested too deeply.");
            }

            return McsYamlNode.ForAlias(anchor.Node, start, end, anchor.SubtreeCount, anchor.SubtreeDepth);
        }

        private static (int Count, int Depth) MeasureSubtree(McsYamlNode node)
        {
            if (node.IsAlias)
            {
                return (node.SubtreeCount, node.SubtreeDepth);
            }

            var count = 1;
            var depth = 0;

            if (node.Properties != null)
            {
                foreach (var property in node.Properties)
                {
                    var (childCount, childDepth) = MeasureSubtree(property.Value);
                    count += 1 + childCount;
                    depth = Math.Max(depth, childDepth);
                }
            }
            else if (node.Items != null)
            {
                foreach (var item in node.Items)
                {
                    var (childCount, childDepth) = MeasureSubtree(item);
                    count += childCount;
                    depth = Math.Max(depth, childDepth);
                }
            }

            return (count, depth + 1);
        }

        private McsYamlNode ParseNodeContent(int nodeIndent, int parentIndent, bool allowBlockMapping, string? tag, McsYamlPosition start, string? firstKeyAnchor, string? firstKeyTag, out bool implicitBlockMapping)
        {
            implicitBlockMapping = false;

            if (AtEnd)
            {
                return McsYamlNode.ForScalar(null, start, Position);
            }

            var current = Current;
            if (current == '|' || current == '>')
            {
                return ApplyTag(tag, ParseBlockScalar(parentIndent));
            }

            if (current == '[' || current == '{')
            {
                return ParseFlowNode(parentIndent);
            }

            if (current == '\'' || current == '"')
            {
                var quoted = ParseQuotedScalar();
                if (allowBlockMapping && IsMappingKeySeparatorAhead())
                {
                    implicitBlockMapping = true;
                    RejectNonTextKeyTag(firstKeyTag, quoted.Start);
                    if (firstKeyAnchor != null)
                    {
                        RegisterAnchor(firstKeyAnchor, quoted);
                    }

                    return ParseBlockMappingFromKey(nodeIndent, quoted);
                }

                return ApplyTag(tag, quoted);
            }

            if (IsSequenceEntry())
            {
                if (!allowBlockMapping)
                {
                    throw Fail("A block sequence cannot appear inline after ':'.");
                }

                return ParseBlockSequence(nodeIndent);
            }

            if (allowBlockMapping && IsIndicatorAlone('?'))
            {
                return ParseBlockMapping(nodeIndent, null);
            }

            if (allowBlockMapping && TryFindKeyEnd())
            {
                implicitBlockMapping = true;
                RejectNonTextKeyTag(firstKeyTag, start);
                return ParseBlockMapping(nodeIndent, firstKeyAnchor);
            }

            return ApplyTag(tag, ParsePlainScalar(parentIndent, tag != null, FlowContext.None));
        }

        private static McsYamlNode ApplyTag(string? tag, McsYamlNode node)
        {
            if (tag == null || node.Kind != McsYamlNodeKind.Scalar || node.Scalar == null)
            {
                return node;
            }

            McsYamlScalars.ResolveTaggedValue(tag, node.Scalar);
            return McsYamlNode.ForTaggedScalar(node.Scalar, node.Start, node.End, tag);
        }

        private bool IsMappingKeySeparatorAhead()
        {
            var restore = Save();
            SkipSpacesAndTabs();
            var found = !AtEnd && Current == ':' && IsKeySeparatorAt(_index, FlowContext.None);
            Restore(restore);
            return found;
        }

        private NodeProperties ReadNodeProperties(bool allowLineBreak) => ReadNodeProperties(allowLineBreak, inFlow: false);

        private NodeProperties ReadNodeProperties(bool allowLineBreak, bool inFlow)
        {
            string? anchor = null;
            string? tagName = null;

            while (!AtEnd)
            {
                if (Current == '&')
                {
                    if (anchor != null)
                    {
                        throw Fail("A node cannot have more than one anchor.");
                    }

                    Advance();
                    anchor = ReadAnchorName();
                }
                else if (Current == '*')
                {
                    Advance();
                    var alias = ReadAnchorName();
                    SkipSpacesAndTabs();
                    return new NodeProperties(null, alias, null, false);
                }
                else if (Current == '!')
                {
                    if (tagName != null)
                    {
                        throw Fail("A node cannot have more than one tag.");
                    }

                    tagName = ReadTagName();
                }
                else
                {
                    break;
                }

                SkipSpacesAndTabs();
                if (AtEnd || IsBreak(Current) || IsCommentStart())
                {
                    if (!allowLineBreak)
                    {
                        return new NodeProperties(anchor, null, tagName, false);
                    }

                    if (inFlow)
                    {
                        SkipFlowSpace();
                        continue;
                    }

                    SkipBlankSpace();
                    return new NodeProperties(anchor, null, tagName, true);
                }
            }

            return new NodeProperties(anchor, null, tagName, false);
        }

        private string ReadTagName()
        {
            Advance();

            if (!AtEnd && Current == '<')
            {
                Advance();
                var uriStart = _index;
                while (!AtEnd && !IsBreak(Current) && Current != '>')
                {
                    Advance();
                }

                if (AtEnd || Current != '>')
                {
                    throw Fail("A verbatim tag must end with '>'.");
                }

                var uri = _text.Substring(uriStart, _index - uriStart);
                Advance();
                RejectTagWithoutSeparator();
                return ResolveTagUri(uri);
            }

            var nameStart = _index;
            while (!AtEnd && !IsBreak(Current) && Current != ' ' && Current != '\t' && !IsFlowDelimiter(Current))
            {
                Advance();
            }

            var name = _text.Substring(nameStart, _index - nameStart);
            RejectTagWithoutSeparator();
            return ResolveTagShorthand(name);
        }

        private void RejectTagWithoutSeparator()
        {
            if (!AtEnd && !IsBreak(Current) && Current != ' ' && Current != '\t')
            {
                throw Fail("A tag must be followed by a space or a line break.");
            }
        }

        private string ResolveTagShorthand(string name)
        {
            string handle;
            string suffix;

            if (name.Length > 0 && name[0] == '!')
            {
                handle = "!!";
                suffix = name.Substring(1);
            }
            else
            {
                var separator = name.IndexOf('!');
                if (separator >= 0)
                {
                    handle = "!" + name.Substring(0, separator) + "!";
                    suffix = name.Substring(separator + 1);
                }
                else
                {
                    handle = "!";
                    suffix = name;
                }
            }

            if (!_tagHandles.TryGetValue(handle, out var prefix))
            {
                throw Fail($"Tag handle '{handle}' has not been declared.");
            }

            return ResolveTagUri(prefix + suffix);
        }

        private string ResolveTagUri(string uri)
        {
            if (uri.StartsWith(StandardTagPrefix, StringComparison.Ordinal))
            {
                switch (uri.Substring(StandardTagPrefix.Length))
                {
                    case "str":
                        return "!!str";
                    case "int":
                        return "!!int";
                    case "bool":
                        return "!!bool";
                    case "float":
                        return "!!float";
                    case "map":
                        return "!!map";
                    case "seq":
                        return "!!seq";
                }
            }

            throw Fail($"Tag '{uri}' cannot be resolved.");
        }

        private string ReadAnchorName()
        {
            var builder = new StringBuilder();
            while (!AtEnd && !IsBreak(Current) && Current != ' ' && Current != '\t' && Current != ',' && Current != '[' && Current != ']' && Current != '{' && Current != '}')
            {
                builder.Append(Current);
                Advance();
            }

            if (builder.Length == 0)
            {
                throw Fail("Expected a name after an anchor or alias indicator.");
            }

            return builder.ToString();
        }

        private McsYamlNode ParseBlockMapping(int indent, string? firstKeyAnchor) => ParseBlockMappingFromKey(indent, null, cursorOnFirstKey: true, firstKeyAnchor);

        private McsYamlNode ParseBlockMappingFromKey(int indent, McsYamlNode? firstKey) => ParseBlockMappingFromKey(indent, firstKey, cursorOnFirstKey: false, null);

        private McsYamlNode ParseBlockMappingFromKey(int indent, McsYamlNode? firstKey, bool cursorOnFirstKey, string? firstKeyAnchor)
        {
            var start = firstKey?.Start ?? Position;
            var properties = new List<McsYamlProperty>();
            var end = start;
            var pendingKey = firstKey;

            while (true)
            {
                McsYamlNode key;
                if (pendingKey != null)
                {
                    key = pendingKey;
                    pendingKey = null;
                }
                else
                {
                    if (!cursorOnFirstKey)
                    {
                        SkipBlankSpace();
                        if (AtEnd || IsDocumentMarker("---") || IsDocumentMarker("..."))
                        {
                            break;
                        }

                        var lineIndent = _column - 1;
                        if (lineIndent < indent)
                        {
                            break;
                        }

                        if (lineIndent > indent)
                        {
                            throw Fail($"Unexpected indentation before '{DescribeCurrentLine()}'.");
                        }

                        if (IsSequenceEntry())
                        {
                            break;
                        }
                    }

                    if (IsIndicatorAlone('?'))
                    {
                        cursorOnFirstKey = false;
                        var explicitProperty = ParseExplicitEntry(indent);
                        properties.Add(explicitProperty);
                        end = explicitProperty.Value.End;
                        continue;
                    }

                    cursorOnFirstKey = false;
                    key = ParseKey();
                }

                RejectUnsupportedKey(key);

                if (firstKeyAnchor != null)
                {
                    RegisterAnchor(firstKeyAnchor, key);
                    firstKeyAnchor = null;
                }

                SkipSpacesAndTabs();
                if (AtEnd || Current != ':')
                {
                    throw Fail("Expected ':' after a mapping key.");
                }

                Advance();
                var value = ParseValueAfterKey(indent);
                properties.Add(new McsYamlProperty(key, value));
                end = value.End;
            }

            return McsYamlNode.ForMapping(properties, start, end);
        }

        private McsYamlProperty ParseExplicitEntry(int indent)
        {
            Advance();
            var key = ParseNode(indent, allowIndentlessSequence: true);
            RejectUnsupportedKey(key);
            SkipBlankSpace();

            if (!AtEnd && _column - 1 == indent && IsIndicatorAlone(':'))
            {
                Advance();
                return new McsYamlProperty(key, ParseValueAfterKey(indent));
            }

            return new McsYamlProperty(key, McsYamlNode.ForScalar(null, key.End, key.End));
        }

        private McsYamlNode ParseKey()
        {
            if (Current == '\'' || Current == '"')
            {
                return ParseQuotedScalar();
            }

            if (Current == '&' || Current == '*' || Current == '!')
            {
                var start = Position;
                var properties = ReadNodeProperties(allowLineBreak: false);
                if (properties.Alias != null)
                {
                    return ResolveAlias(properties.Alias, start, Position);
                }

                var key = Current == '\'' || Current == '"' ? ParseQuotedScalar() : ParsePlainScalar(int.MaxValue, properties.HasTag, FlowContext.BlockKey);
                if (properties.Anchor != null)
                {
                    RegisterAnchor(properties.Anchor, key);
                }

                return properties.Tag == null ? key : ApplyTag(properties.Tag, key);
            }

            return ParsePlainScalar(int.MaxValue, false, FlowContext.BlockKey);
        }

        private void RejectNonTextKeyTag(string? tag, McsYamlPosition position)
        {
            if (tag != null && !string.Equals(tag, "!!str", StringComparison.Ordinal))
            {
                throw new McsYamlFormatException($"A key tagged '{tag}' is not a text key, and only text keys are supported in this document.", position.Line, position.Column);
            }
        }

        private void RejectUnsupportedKey(McsYamlNode key)
        {
            if (key.Kind != McsYamlNodeKind.Scalar || key.Scalar == null)
            {
                throw new McsYamlFormatException("Only text keys are supported in this document.", key.Start.Line, key.Start.Column);
            }

            if (key.Tag != null && !string.Equals(key.Tag, "!!str", StringComparison.Ordinal))
            {
                throw new McsYamlFormatException($"A key tagged '{key.Tag}' is not a text key, and only text keys are supported in this document.", key.Start.Line, key.Start.Column);
            }
        }

        private McsYamlNode ParseValueAfterKey(int keyIndent)
        {
            if (!AtEnd && !IsBreak(Current) && Current != ' ' && Current != '\t')
            {
                throw Fail("Expected a space after ':'.");
            }

            var separatorPosition = Position;
            SkipSpacesAndTabs();

            if (AtEnd || IsBreak(Current) || IsCommentStart())
            {
                return ParseNode(keyIndent, allowIndentlessSequence: true, separatorPosition);
            }

            return ParseNodeAtCursor(_column - 1, keyIndent, allowBlockMapping: false, allowIndentlessSequence: true);
        }

        private McsYamlNode ParseBlockSequence(int indent)
        {
            var start = Position;
            var items = new List<McsYamlNode>();
            var end = start;

            while (true)
            {
                SkipBlankSpace();
                if (AtEnd || IsDocumentMarker("---") || IsDocumentMarker("..."))
                {
                    break;
                }

                if (_column - 1 != indent || !IsSequenceEntry())
                {
                    break;
                }

                Advance();
                McsYamlNode item;

                if (AtEnd || IsBreak(Current) || IsCommentStart())
                {
                    item = ParseNode(indent, allowIndentlessSequence: false);
                }
                else
                {
                    SkipSpacesAndTabs();
                    item = AtEnd || IsBreak(Current) || IsCommentStart()
                        ? ParseNode(indent, allowIndentlessSequence: false)
                        : ParseNodeAtCursor(_column - 1, indent, allowBlockMapping: true);
                }

                items.Add(item);
                end = item.End;
            }

            return McsYamlNode.ForSequence(items, start, end);
        }

        private McsYamlNode ParseBlockScalar(int parentIndent)
        {
            var start = Position;
            var literal = Current == '|';
            Advance();

            var explicitIndent = 0;
            var chomping = ' ';

            for (var indicator = 0; indicator < 2 && !AtEnd; indicator++)
            {
                if (Current >= '1' && Current <= '9' && explicitIndent == 0)
                {
                    explicitIndent = Current - '0';
                    Advance();
                }
                else if ((Current == '-' || Current == '+') && chomping == ' ')
                {
                    chomping = Current;
                    Advance();
                }
                else
                {
                    break;
                }
            }

            SkipSpacesAndTabs();
            if (IsCommentStart())
            {
                SkipToBreak();
            }

            if (!AtEnd && !IsBreak(Current))
            {
                throw Fail("Unexpected content after a block scalar header.");
            }

            ConsumeBreak();

            var lines = new List<BlockLine>();
            var contentIndent = explicitIndent > 0 ? Math.Max(parentIndent, 0) + explicitIndent : -1;
            var pendingBlankLines = new List<(int Spaces, bool Terminated, char Break)>();

            while (!AtEnd)
            {
                var lineStart = _index;
                var spaces = 0;
                while (!AtEnd && Current == ' ')
                {
                    spaces++;
                    Advance();
                }

                var blank = AtEnd || IsBreak(Current);

                if (blank && contentIndent < 0)
                {
                    var stillOpen = !AtEnd;
                    pendingBlankLines.Add((spaces, stillOpen, stillOpen ? NormalizedBreakCharacter(Current) : '\n'));
                    ConsumeBreak();
                    if (!stillOpen)
                    {
                        break;
                    }

                    continue;
                }

                if (!blank && contentIndent < 0)
                {
                    if (spaces <= parentIndent)
                    {
                        _index = lineStart;
                        _column -= spaces;
                        break;
                    }

                    contentIndent = spaces;
                    foreach (var pending in pendingBlankLines)
                    {
                        if (pending.Spaces > contentIndent)
                        {
                            throw Fail("A leading empty line in a block scalar cannot be indented more than its content.");
                        }

                        lines.Add(new BlockLine(string.Empty, pending.Terminated, pending.Break));
                    }

                    pendingBlankLines.Clear();
                }

                if (!blank && spaces < contentIndent)
                {
                    _index = lineStart;
                    _column -= spaces;
                    break;
                }

                var builder = new StringBuilder();
                if (spaces > contentIndent)
                {
                    builder.Append(' ', spaces - contentIndent);
                }

                while (!AtEnd && !IsBreak(Current))
                {
                    builder.Append(Current);
                    Advance();
                }

                var terminated = !AtEnd;
                var breakCharacter = AtEnd ? '\n' : NormalizedBreakCharacter(Current);
                ConsumeBreak();
                lines.Add(new BlockLine(builder.ToString(), terminated, breakCharacter));

                if (!terminated)
                {
                    break;
                }
            }

            foreach (var pending in pendingBlankLines)
            {
                lines.Add(new BlockLine(string.Empty, pending.Terminated, pending.Break));
            }

            var value = literal ? BuildLiteral(lines, chomping) : BuildFolded(lines, chomping);
            return McsYamlNode.ForScalar(value, start, Position);
        }

        private static string BuildLiteral(List<BlockLine> lines, char chomping)
        {
            var builder = new StringBuilder();
            foreach (var line in lines)
            {
                builder.Append(line.Text);
                if (line.Terminated)
                {
                    builder.Append(line.BreakCharacter);
                }
            }

            return ApplyChomping(builder.ToString(), chomping);
        }

        private static string BuildFolded(List<BlockLine> lines, char chomping)
        {
            var lastContent = -1;
            for (var index = 0; index < lines.Count; index++)
            {
                if (lines[index].Text.Length > 0)
                {
                    lastContent = index;
                }
            }

            var body = new StringBuilder();
            var written = false;
            var previousMoreIndented = false;
            var pendingBreaks = new List<char>();

            for (var index = 0; index <= lastContent; index++)
            {
                var line = lines[index];
                if (line.Text.Length == 0)
                {
                    pendingBreaks.Add(line.BreakCharacter);
                    continue;
                }

                var moreIndented = line.Text[0] == ' ' || line.Text[0] == '\t';
                if (written)
                {
                    AppendFoldedBreaks(body, pendingBreaks, moreIndented || previousMoreIndented);
                }
                else
                {
                    foreach (var pending in pendingBreaks)
                    {
                        body.Append(pending);
                    }
                }

                pendingBreaks.Clear();
                body.Append(line.Text);
                if (line.Terminated)
                {
                    pendingBreaks.Add(line.BreakCharacter);
                }

                written = true;
                previousMoreIndented = moreIndented;
            }

            for (var index = lastContent + 1; index < lines.Count; index++)
            {
                if (lines[index].Terminated)
                {
                    pendingBreaks.Add(lines[index].BreakCharacter);
                }
            }

            foreach (var pending in pendingBreaks)
            {
                body.Append(pending);
            }

            return ApplyChomping(body.ToString(), chomping);
        }

        private static void AppendFoldedBreaks(StringBuilder builder, List<char> breaks, bool forceLineBreak)
        {
            if (breaks.Count == 0)
            {
                return;
            }

            if (PreservedBreakCharacter(breaks[0]) != '\0')
            {
                builder.Append(breaks[0]);
            }
            else if (forceLineBreak)
            {
                builder.Append('\n');
            }
            else if (breaks.Count == 1)
            {
                builder.Append(' ');
            }

            for (var index = 1; index < breaks.Count; index++)
            {
                builder.Append(breaks[index]);
            }
        }

        private static string ApplyChomping(string raw, char chomping)
        {
            if (chomping == '+')
            {
                return raw;
            }

            var end = raw.Length;
            while (end > 0 && IsBreak(raw[end - 1]))
            {
                end--;
            }

            if (chomping == '-' || end == raw.Length)
            {
                return raw.Substring(0, end);
            }

            return end == 0 ? string.Empty : raw.Substring(0, end) + raw[end];
        }

        private McsYamlNode ParseQuotedScalar()
        {
            var start = Position;
            var quote = Current;
            Advance();

            var builder = new StringBuilder();
            var pendingBreaks = new List<char>();
            var foldSuppressed = false;
            var protectedLength = 0;

            while (true)
            {
                if (AtEnd)
                {
                    throw new McsYamlFormatException("Unterminated quoted scalar.", start.Line, start.Column);
                }

                if (IsBreak(Current))
                {
                    pendingBreaks.Add(NormalizedBreakCharacter(Current));
                    ConsumeBreak();
                    SkipSpacesAndTabs();
                    if (AtEnd)
                    {
                        throw new McsYamlFormatException("Unterminated quoted scalar.", start.Line, start.Column);
                    }

                    if (IsDocumentMarker("---") || IsDocumentMarker("..."))
                    {
                        throw Fail("A document marker cannot appear inside a quoted scalar.");
                    }

                    continue;
                }

                if (pendingBreaks.Count > 0)
                {
                    TrimTrailingSpaces(builder, protectedLength);
                    if (foldSuppressed)
                    {
                        foreach (var pending in pendingBreaks.Skip(1))
                        {
                            builder.Append(pending);
                        }
                    }
                    else
                    {
                        AppendFoldedBreaks(builder, pendingBreaks, forceLineBreak: false);
                    }

                    pendingBreaks.Clear();
                    foldSuppressed = false;
                }

                if (Current == quote)
                {
                    if (quote == '\'' && At(1) == '\'')
                    {
                        builder.Append('\'');
                        Advance();
                        Advance();
                        continue;
                    }

                    Advance();
                    return McsYamlNode.ForScalar(builder.ToString(), start, Position);
                }

                if (quote == '"' && Current == '\\')
                {
                    Advance();
                    if (!AtEnd && IsBreak(Current))
                    {
                        pendingBreaks.Add(NormalizedBreakCharacter(Current));
                        ConsumeBreak();
                        SkipSpacesAndTabs();
                        protectedLength = builder.Length;
                        foldSuppressed = true;
                        continue;
                    }

                    AppendEscape(builder);
                    protectedLength = builder.Length;
                    continue;
                }

                builder.Append(Current);
                Advance();
            }
        }

        private void AppendEscape(StringBuilder builder)
        {
            if (AtEnd)
            {
                throw Fail("Unterminated escape sequence.");
            }

            var escape = Current;
            Advance();

            switch (escape)
            {
                case '0': builder.Append('\0'); return;
                case 'a': builder.Append('\a'); return;
                case 'b': builder.Append('\b'); return;
                case 't': builder.Append('\t'); return;
                case '\t': builder.Append('\t'); return;
                case 'n': builder.Append('\n'); return;
                case 'v': builder.Append('\v'); return;
                case 'f': builder.Append('\f'); return;
                case 'r': builder.Append('\r'); return;
                case 'e': builder.Append('\u001b'); return;
                case ' ': builder.Append(' '); return;
                case '"': builder.Append('"'); return;
                case '/': builder.Append('/'); return;
                case '\\': builder.Append('\\'); return;
                case 'N': builder.Append('\u0085'); return;
                case '_': builder.Append('\u00a0'); return;
                case 'L': builder.Append('\u2028'); return;
                case 'P': builder.Append('\u2029'); return;
                case 'x': builder.Append(ReadCodePoint(2)); return;
                case 'u': builder.Append(ReadCodePoint(4)); return;
                case 'U': builder.Append(ReadCodePoint(8)); return;
                default:
                    throw Fail($"Unsupported escape sequence '\\{escape}'.");
            }
        }

        private string ReadCodePoint(int length)
        {
            uint value = 0;
            for (var digit = 0; digit < length; digit++)
            {
                if (AtEnd || !Uri.IsHexDigit(Current))
                {
                    throw Fail("Expected a hexadecimal escape sequence.");
                }

                value = (value * 16) + (uint)Convert.ToInt32(Current.ToString(), 16);
                Advance();
            }

            if (value > 0x10FFFF || (value >= 0xD800 && value <= 0xDFFF))
            {
                throw Fail($"'{value:X}' is not a valid Unicode code point.");
            }

            return char.ConvertFromUtf32((int)value);
        }

        private McsYamlNode ParsePlainScalar(int parentIndent, bool hasTag, FlowContext context)
        {
            var start = Position;
            RejectReservedIndicator();

            var builder = new StringBuilder();
            var pendingBreaks = new List<char>();

            while (true)
            {
                if (AtEnd || IsBreak(Current))
                {
                    if (context == FlowContext.BlockKey || (context == FlowContext.None && parentIndent == int.MaxValue))
                    {
                        break;
                    }

                    var restore = Save();
                    var breaks = new List<char>();
                    while (!AtEnd && IsBreak(Current))
                    {
                        breaks.Add(NormalizedBreakCharacter(Current));
                        ConsumeBreak();
                        if (!AtEnd && Current == '\t')
                        {
                            throw Fail("Tabs are not allowed in indentation.");
                        }

                        SkipSpacesAndTabs();
                    }

                    if (AtEnd || IsDocumentMarker("---") || IsDocumentMarker("...") || (context == FlowContext.None && _column - 1 <= parentIndent))
                    {
                        Restore(restore);
                        break;
                    }

                    if (context != FlowContext.None && StartsPlainScalar(Current))
                    {
                        RejectFlowContinuationIndent();
                    }

                    pendingBreaks = breaks;
                    continue;
                }

                if (IsCommentStart() && builder.Length > 0)
                {
                    break;
                }

                if (Current == ':' && IsKeySeparatorAt(_index, context))
                {
                    if (context == FlowContext.BlockKey || context == FlowContext.FlowKey)
                    {
                        break;
                    }

                    throw Fail("Unexpected ':' in a plain value; quote the value to include it.");
                }

                if (context != FlowContext.None && context != FlowContext.BlockKey && IsFlowDelimiter(Current))
                {
                    break;
                }

                if (pendingBreaks.Count > 0)
                {
                    TrimTrailingSpaces(builder);
                    AppendFoldedBreaks(builder, pendingBreaks, forceLineBreak: false);
                    pendingBreaks = new List<char>();
                }

                builder.Append(Current);
                Advance();
            }

            TrimTrailingSpaces(builder);
            var text = builder.ToString();
            var end = Position;

            if (text.Length == 0)
            {
                return McsYamlNode.ForScalar(hasTag ? string.Empty : null, start, end);
            }

            var isKey = !hasTag && (context == FlowContext.BlockKey || context == FlowContext.FlowKey);
            return McsYamlNode.ForScalar(!isKey && McsYamlScalars.IsNullLiteral(text) ? null : text, start, end);
        }

        private void RejectReservedIndicator()
        {
            if (Current == '@' || Current == '`' || Current == '%' || Current == '#' || Current == ']' || Current == '}' || Current == ',')
            {
                throw Fail($"'{Current}' cannot start a plain scalar.");
            }

            if (IsIndicatorAlone('-') || IsIndicatorAlone('?') || IsIndicatorAlone(':'))
            {
                throw Fail($"'{Current}' cannot start a plain scalar.");
            }
        }

        private McsYamlNode ParseFlowNode() => ParseFlowNode(_flowParentIndent);

        private McsYamlNode ParseFlowNode(int parentIndent)
        {
            if (_depth++ > MaximumDepth)
            {
                throw Fail("The document is nested too deeply.");
            }

            var restoreIndent = _flowParentIndent;
            _flowParentIndent = parentIndent;

            try
            {
                return Current == '[' ? ParseFlowSequence() : ParseFlowMapping();
            }
            finally
            {
                _flowParentIndent = restoreIndent;
                _depth--;
            }
        }

        private McsYamlNode ParseFlowSequence()
        {
            var start = Position;
            Advance();
            var items = new List<McsYamlNode>();

            while (true)
            {
                SkipFlowSpace();
                if (AtEnd)
                {
                    throw new McsYamlFormatException("Unterminated flow sequence.", start.Line, start.Column);
                }

                if (Current == ']')
                {
                    Advance();
                    return McsYamlNode.ForSequence(items, start, Position);
                }

                var entry = ParseFlowEntry();
                items.Add(entry);

                SkipFlowSpace();
                if (!AtEnd && Current == ',')
                {
                    Advance();
                    continue;
                }

                if (!AtEnd && Current == ']')
                {
                    Advance();
                    return McsYamlNode.ForSequence(items, start, Position);
                }

                throw Fail("Expected ',' or ']' in a flow sequence.");
            }
        }

        private McsYamlNode ParseFlowMapping()
        {
            var start = Position;
            Advance();
            var properties = new List<McsYamlProperty>();

            while (true)
            {
                SkipFlowSpace();
                if (AtEnd)
                {
                    throw new McsYamlFormatException("Unterminated flow mapping.", start.Line, start.Column);
                }

                if (Current == '}')
                {
                    Advance();
                    return McsYamlNode.ForMapping(properties, start, Position);
                }

                var key = ParseFlowKey();
                SkipFlowSpace();

                McsYamlNode value;
                if (!AtEnd && Current == ':')
                {
                    value = ParseFlowPairValue();
                }
                else
                {
                    value = McsYamlNode.ForScalar(null, Position, Position);
                }

                RejectUnsupportedKey(key);
                properties.Add(new McsYamlProperty(key, value));
                SkipFlowSpace();

                if (!AtEnd && Current == ',')
                {
                    Advance();
                    continue;
                }

                if (!AtEnd && Current == '}')
                {
                    Advance();
                    return McsYamlNode.ForMapping(properties, start, Position);
                }

                throw Fail("Expected ',' or '}' in a flow mapping.");
            }
        }

        private McsYamlNode ParseFlowKey()
        {
            SkipFlowSpace();

            if (IsIndicatorAlone('?'))
            {
                Advance();
                SkipFlowSpace();
            }

            return ParseFlowScalarOrNode(FlowContext.FlowKey);
        }

        private McsYamlNode ParseFlowEntry()
        {
            SkipFlowSpace();

            if (IsIndicatorAlone('?'))
            {
                Advance();
                SkipFlowSpace();
                var explicitKey = ParseFlowScalarOrNode(FlowContext.FlowKey);
                RejectUnsupportedKey(explicitKey);
                SkipFlowSpace();
                var explicitValue = ParseFlowPairValue();
                return McsYamlNode.ForMapping(new[] { new McsYamlProperty(explicitKey, explicitValue) }, explicitKey.Start, explicitValue.End);
            }

            var key = ParseFlowScalarOrNode(FlowContext.FlowKey, out var plainScalar, out var anchor);
            SkipFlowSpace();

            if (AtEnd || Current != ':')
            {
                var value = plainScalar && key.Scalar != null && McsYamlScalars.IsNullLiteral(key.Scalar)
                    ? McsYamlNode.ForScalar(null, key.Start, key.End)
                    : key;

                if (anchor != null)
                {
                    RegisterAnchor(anchor, value);
                }

                return value;
            }

            if (anchor != null)
            {
                RegisterAnchor(anchor, key);
            }

            RejectUnsupportedKey(key);
            var pairValue = ParseFlowPairValue();
            return McsYamlNode.ForMapping(new[] { new McsYamlProperty(key, pairValue) }, key.Start, pairValue.End);
        }

        private McsYamlNode ParseFlowPairValue()
        {
            if (AtEnd || Current != ':')
            {
                return McsYamlNode.ForScalar(null, Position, Position);
            }

            Advance();
            SkipFlowSpace();
            return AtEnd || Current == ',' || Current == ']' || Current == '}'
                ? McsYamlNode.ForScalar(null, Position, Position)
                : ParseFlowScalarOrNode(FlowContext.FlowValue);
        }

        private McsYamlNode ParseFlowScalarOrNode(FlowContext context)
        {
            var node = ParseFlowScalarOrNode(context, out _, out var anchor);
            if (anchor != null)
            {
                RegisterAnchor(anchor, node);
            }

            return node;
        }

        private McsYamlNode ParseFlowScalarOrNode(FlowContext context, out bool plainScalar, out string? anchor)
        {
            plainScalar = false;
            var start = Position;
            var properties = ReadNodeProperties(allowLineBreak: true, inFlow: true);
            anchor = properties.Anchor;

            if (properties.Alias != null)
            {
                return ResolveAlias(properties.Alias, start, Position);
            }

            McsYamlNode node;
            if ((properties.Anchor != null || properties.HasTag) && (AtEnd || Current == ',' || Current == ']' || Current == '}'))
            {
                node = McsYamlNode.ForScalar(null, start, Position);
            }
            else if (Current == '[' || Current == '{')
            {
                node = ParseFlowNode();
            }
            else if (Current == '\'' || Current == '"')
            {
                node = ParseQuotedScalar();
            }
            else
            {
                node = ParsePlainScalar(int.MaxValue, properties.HasTag, context);
                if (node.Scalar == null && !properties.HasTag && node.Start.Index == node.End.Index)
                {
                    throw Fail("Expected a value in a flow collection.");
                }

                plainScalar = !properties.HasTag;
            }

            return properties.Tag == null ? node : ApplyTag(properties.Tag, node);
        }

        private bool IsKeySeparatorAt(int index, FlowContext context)
        {
            var next = index + 1 < _text.Length ? _text[index + 1] : '\0';
            if (next == '\0' || next == ' ' || next == '\t' || IsBreak(next))
            {
                return true;
            }

            return context != FlowContext.None && context != FlowContext.BlockKey && next == ',';
        }

        private static bool IsFlowDelimiter(char value) => value == ',' || value == '[' || value == ']' || value == '{' || value == '}';

        private bool TryFindKeyEnd()
        {
            var restore = Save();
            try
            {
                if (Current == '\'' || Current == '"')
                {
                    try
                    {
                        ParseQuotedScalar();
                    }
                    catch (McsYamlFormatException)
                    {
                        return false;
                    }

                    SkipSpacesAndTabs();
                    return !AtEnd && Current == ':' && IsKeySeparatorAt(_index, FlowContext.None);
                }

                while (!AtEnd && !IsBreak(Current))
                {
                    if (IsCommentStart())
                    {
                        return false;
                    }

                    if (Current == ':' && IsKeySeparatorAt(_index, FlowContext.None))
                    {
                        return true;
                    }

                    Advance();
                }

                return false;
            }
            finally
            {
                Restore(restore);
            }
        }

        private bool IsSequenceEntry() => IsIndicatorAlone('-');

        private bool IsIndicatorAlone(char indicator)
        {
            if (Current != indicator)
            {
                return false;
            }

            var next = At(1);
            return next == '\0' || next == ' ' || next == '\t' || IsBreak(next);
        }

        private bool IsDocumentMarker(string marker)
        {
            if (_column != 1 || _index + 3 > _text.Length)
            {
                return false;
            }

            if (string.CompareOrdinal(_text, _index, marker, 0, 3) != 0)
            {
                return false;
            }

            var next = At(3);
            return next == '\0' || next == ' ' || next == '\t' || IsBreak(next);
        }

        private bool IsCommentStart()
        {
            if (Current != '#')
            {
                return false;
            }

            if (_index == 0 || (_index == 1 && _text.Length > 0 && _text[0] == '\uFEFF'))
            {
                return true;
            }

            var previous = _text[_index - 1];
            return previous == ' ' || previous == '\t' || IsBreak(previous);
        }

        private bool IsFlowCommentStart() => Current == '#' && (IsCommentStart() || _text[_index - 1] == '[' || _text[_index - 1] == '{');

        private void SkipSpacesAndTabs()
        {
            while (!AtEnd && (Current == ' ' || Current == '\t'))
            {
                Advance();
            }
        }

        private void SkipFlowSpace()
        {
            var crossedLineBreak = false;

            while (!AtEnd)
            {
                if (Current == ' ' || Current == '\t')
                {
                    Advance();
                }
                else if (IsBreak(Current))
                {
                    ConsumeBreak();
                    crossedLineBreak = true;
                }
                else if (IsFlowCommentStart())
                {
                    SkipToBreak();
                }
                else
                {
                    if (crossedLineBreak && StartsPlainScalar(Current))
                    {
                        RejectFlowContinuationIndent();
                    }

                    return;
                }
            }
        }

        private static bool StartsPlainScalar(char character) => character switch
        {
            '\'' or '"' or '[' or ']' or '{' or '}' or ',' or '&' or '*' or '!' => false,
            _ => true,
        };

        private void RejectFlowContinuationIndent()
        {
            if (_column - 1 <= _flowParentIndent)
            {
                throw Fail("A flow collection cannot continue at or before the indentation of its parent.");
            }
        }

        private void SkipBlankSpace()
        {
            while (!AtEnd)
            {
                var restore = Save();
                var spaces = 0;
                while (!AtEnd && Current == ' ')
                {
                    spaces++;
                    Advance();
                }

                if (!AtEnd && Current == '\t')
                {
                    var probe = Save();
                    SkipSpacesAndTabs();
                    if (!AtEnd && !IsBreak(Current) && !IsCommentStart())
                    {
                        Restore(probe);
                        throw Fail("Tabs are not allowed in indentation.");
                    }
                }

                if (AtEnd)
                {
                    return;
                }

                if (IsBreak(Current))
                {
                    ConsumeBreak();
                    continue;
                }

                if (IsCommentStart())
                {
                    SkipToLineEnd();
                    continue;
                }

                Restore(restore);
                SkipSpacesAndTabs();
                return;
            }
        }

        private void SkipToBreak()
        {
            while (!AtEnd && !IsBreak(Current))
            {
                Advance();
            }
        }

        private void SkipToLineEnd()
        {
            SkipToBreak();
            ConsumeBreak();
        }

        private void ConsumeBreak()
        {
            if (AtEnd)
            {
                return;
            }

            if (Current == '\r')
            {
                Advance();
                if (!AtEnd && Current == '\n')
                {
                    Advance();
                }

                return;
            }

            if (IsBreak(Current))
            {
                Advance();
            }
        }

        private void ConsumeCount(int count)
        {
            for (var step = 0; step < count; step++)
            {
                Advance();
            }
        }

        private static void TrimTrailingSpaces(StringBuilder builder) => TrimTrailingSpaces(builder, 0);

        private static void TrimTrailingSpaces(StringBuilder builder, int protectedLength)
        {
            while (builder.Length > protectedLength && (builder[builder.Length - 1] == ' ' || builder[builder.Length - 1] == '\t'))
            {
                builder.Length--;
            }
        }

        private static bool IsBreak(char value) => value == '\n' || value == '\r' || value == '\u0085' || value == '\u2028' || value == '\u2029';

        private static char PreservedBreakCharacter(char value) => value == '\u2028' || value == '\u2029' ? value : '\0';

        private static char NormalizedBreakCharacter(char value) => value == '\u2028' || value == '\u2029' ? value : '\n';

        private bool AtEnd => _index >= _text.Length;

        private char Current => _index < _text.Length ? _text[_index] : '\0';

        private char At(int offset) => _index + offset < _text.Length ? _text[_index + offset] : '\0';

        private McsYamlPosition Position => new(_line, _column, _index);

        private void Advance()
        {
            if (_index >= _text.Length)
            {
                return;
            }

            var value = _text[_index];
            if (value == '\n' || ((value == '\r' || value == '\u0085' || value == '\u2028' || value == '\u2029') && At(1) != '\n'))
            {
                _line++;
                _column = 1;
            }
            else if (value != '\r')
            {
                _column++;
            }

            _index++;
        }

        private State Save() => new(_index, _line, _column);

        private void Restore(State state)
        {
            _index = state.Index;
            _line = state.Line;
            _column = state.Column;
        }

        private string DescribeCurrentLine()
        {
            var stop = _index;
            while (stop < _text.Length && !IsBreak(_text[stop]))
            {
                stop++;
            }

            var text = _text.Substring(_index, stop - _index).Trim();
            return text.Length > 40 ? text.Substring(0, 40) + "..." : text;
        }

        private McsYamlFormatException Fail(string message) => new(message, _line, _column);

        private enum FlowContext
        {
            None,
            BlockKey,
            FlowKey,
            FlowValue,
        }

        private sealed class AnchorInfo
        {
            public AnchorInfo(McsYamlNode node)
            {
                Node = node;
            }

            public McsYamlNode Node { get; }

            public int SubtreeCount { get; set; } = -1;

            public int SubtreeDepth { get; set; } = -1;
        }

        private readonly struct NodeProperties
        {
            public NodeProperties(string? anchor, string? alias, string? tag, bool crossedLineBreak)
            {
                Anchor = anchor;
                Alias = alias;
                Tag = tag;
                CrossedLineBreak = crossedLineBreak;
            }

            public string? Anchor { get; }

            public string? Alias { get; }

            public string? Tag { get; }

            public bool HasTag => Tag != null;

            public bool CrossedLineBreak { get; }
        }

        private readonly struct BlockLine
        {
            public BlockLine(string text, bool terminated, char breakCharacter)
            {
                Text = text;
                Terminated = terminated;
                BreakCharacter = breakCharacter;
            }

            public string Text { get; }

            public bool Terminated { get; }

            public char BreakCharacter { get; }
        }

        private readonly struct State
        {
            public State(int index, int line, int column)
            {
                Index = index;
                Line = line;
                Column = column;
            }

            public int Index { get; }

            public int Line { get; }

            public int Column { get; }
        }
    }
}

/// <summary>Reports YAML that the agent metadata reader cannot represent, with the source line and column.</summary>
internal sealed class McsYamlFormatException : Exception
{
    public McsYamlFormatException(string message) : this(message, 1, 1)
    {
    }

    public McsYamlFormatException(string message, int line, int column) : base(message)
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }

    public int Column { get; }
}
