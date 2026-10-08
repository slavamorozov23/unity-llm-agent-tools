using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.Editor
{
    // The graph as code, read and written in one piece: a line per node "7c6c = Multiply(A: 3a1f, B: _Tint);" (settings
    // first, as the node draws them, then inputs), groups as "group "Title" { ... }", stack blocks as "Base Color = 7c6c;".
    // Written back, only what differs changes: a line of an existing node sets that node, a new name adds nodes, HLSL
    // expressions become the nodes they name, and nodes of a written group that the text no longer mentions are deleted.
    // The graph stays ordinary nodes, so it reads and edits in Shader Graph as before.
    internal sealed partial class ShaderGraphView
    {
        private static readonly Regex PlainWord = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$");
        private static readonly Regex HlslType = new Regex(@"^(float|half|real|int|uint|bool|double|min16float|min10float)([1-4](x[1-4])?)?$");
        private static readonly Regex Swizzle = new Regex(@"^([xyzw]{1,4}|[rgba]{1,4})$");

        // ---------- print ----------

        private static string[] GraphCode(Model model, object onlyGroup)
        {
            var printed = model.nodes.Where(node => !model.IsBlock(node) && !IsPropertyNode(node) && !IsSubGraphOutput(node) &&
                (onlyGroup == null || Sg.Get(node, "group") == onlyGroup)).ToList();
            var order = SourcesFirst(model, printed);
            var lines = new List<string>();
            var done = new HashSet<object>();
            foreach (var node in order)
            {
                if (done.Contains(node))
                    continue;
                var group = Sg.Get(node, "group");
                if (group == null)
                {
                    lines.AddRange(NodeCode(model, node, string.Empty));
                    done.Add(node);
                    continue;
                }
                lines.Add("group " + Quote(GroupTitle(model.graph, group)) + " {");
                foreach (var member in order.Where(item => Sg.Get(item, "group") == group))
                {
                    lines.AddRange(NodeCode(model, member, "    "));
                    done.Add(member);
                }
                lines.Add("}");
            }
            // The stack (or the sub graph's outputs): all of it for the graph, what the group feeds for a group.
            foreach (var target in model.nodes.Where(node => model.IsBlock(node) || IsSubGraphOutput(node)))
                foreach (var slot in model.Inputs(target))
                {
                    var source = model.IncomingEdges(target, slot).Select(edge => Sg.Get(Sg.Get(edge, "outputSlot"), "node")).FirstOrDefault();
                    if (onlyGroup != null && (source == null || Sg.Get(source, "group") != onlyGroup))
                        continue;
                    var value = InputCode(model, target, slot);
                    if (value != null)
                        lines.Add((model.IsBlock(target) ? model.Label(target) : Sg.Get(target, "name") + "." + SlotName(slot)) + " = " + value + ";");
                }
            return lines.ToArray();
        }

        // Groups of one title are told apart by their order, as same-named scene objects are: "Group[0]", "Group[1]".
        private static string GroupTitle(object graph, object group)
        {
            var title = (string)Sg.Get(group, "title");
            var same = Sg.Items(Sg.Get(graph, "groups")).Where(item => (string)Sg.Get(item, "title") == title).ToList();
            return same.Count > 1 ? title + "[" + same.IndexOf(group) + "]" : title;
        }

        private static object FindGroup(object graph, string title)
        {
            var groups = Sg.Items(Sg.Get(graph, "groups")).ToList();
            var exact = groups.Where(item => (string)Sg.Get(item, "title") == title).ToList();
            var indexed = Regex.Match(title, @"^(.*)\[(\d+)\]$");
            if (exact.Count == 0 && indexed.Success)
            {
                var same = groups.Where(item => (string)Sg.Get(item, "title") == indexed.Groups[1].Value).ToList();
                var index = int.Parse(indexed.Groups[2].Value, CultureInfo.InvariantCulture);
                return same.Count > 1 && index < same.Count ? same[index] : null;
            }
            if (exact.Count == 0)
                exact = groups.Where(item => AssetViews.KeyIs(title, (string)Sg.Get(item, "title"))).ToList();
            if (exact.Count > 1)
                throw new ArgumentException("Several groups are titled " + title + "; use " + string.Join(", ", exact.Select(item => GroupTitle(graph, item))));
            return exact.FirstOrDefault();
        }

        private static IEnumerable<string> NodeCode(Model model, object node, string indent)
        {
            var custom = node.GetType().Name == "CustomFunctionNode";
            var args = new List<string>();
            foreach (var control in Controls(node))
            {
                var value = control.label == "Body" ? null : ControlValue(node, control);
                if (value != null)
                    args.Add(control.label + ": " + ValueCode(value is bool ? ((bool)value ? "true" : "false") : Convert.ToString(value, CultureInfo.InvariantCulture)));
            }
            if (custom)
            {
                if (model.Inputs(node).Count > 0)
                    args.Add("Inputs: " + Quote(SlotList(model.Inputs(node))));
                if (model.Outputs(node).Count > 0)
                    args.Add("Outputs: " + Quote(SlotList(model.Outputs(node))));
            }
            foreach (var slot in model.Inputs(node))
            {
                var value = InputCode(model, node, slot);
                if (value != null)
                    args.Add(SlotName(slot) + ": " + value);
            }
            // A Custom Function is named "Haze (Custom Function)" on its title bar; the code names the node it is.
            var line = indent + model.Id(node) + " = " + (custom ? "Custom Function" : Sg.Get(node, "name")) + "(" + string.Join(", ", args) + ")";
            var body = custom && Sg.Get(node, "sourceType").ToString() != "File" ? (string)Sg.Get(node, "functionBody") : null;
            if (body == null)
            {
                yield return line + ";";
                yield break;
            }
            yield return line + " {";
            foreach (var text in Dedent(body))
                yield return indent + "    " + text;
            yield return indent + "}";
        }

        // A wire as the source's id (".Slot" when it has several outputs), a property by its reference, else the typed value.
        private static string InputCode(Model model, object node, object slot)
        {
            var edge = model.IncomingEdges(node, slot).FirstOrDefault();
            if (edge == null)
            {
                var value = SlotValue(slot) as string;
                return value == null ? null : ValueCode(value);
            }
            var output = Sg.Get(edge, "outputSlot");
            var source = Sg.Get(output, "node");
            if (!model.nodes.Contains(source))
                return null;
            if (IsPropertyNode(source))
                return (string)Sg.Get(Sg.Get(source, "property"), "referenceName");
            var sourceSlot = Sg.Get(output, "slot");
            return model.Id(source) + (model.Outputs(source).Count == 1 ? string.Empty : "." + (sourceSlot == null ? Sg.Get(output, "slotId").ToString() : SlotName(sourceSlot)));
        }

        // "0.5" stays a number, "1,0,0" becomes float3(1, 0, 0), a name stays bare, anything else is quoted.
        private static string ValueCode(string value)
        {
            var parts = value.Split(',').Select(part => part.Trim()).ToArray();
            if (parts.All(part => float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                return parts.Length == 1 ? parts[0] : "float" + parts.Length + "(" + string.Join(", ", parts) + ")";
            return PlainWord.IsMatch(value) ? value : Quote(value);
        }

        private static string SlotList(IEnumerable<object> slots)
        {
            return string.Join(", ", slots.Select(slot => SlotName(slot) + ": " + TypeName(slot)));
        }

        private static string Quote(string text)
        {
            return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", string.Empty).Replace("\n", "\\n") + "\"";
        }

        private static IEnumerable<string> Dedent(string text)
        {
            var lines = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n').Select(line => line.TrimEnd()).ToList();
            while (lines.Count > 0 && lines[0].Length == 0)
                lines.RemoveAt(0);
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                lines.RemoveAt(lines.Count - 1);
            var margin = lines.Where(line => line.Length > 0).Select(line => line.Length - line.TrimStart().Length).DefaultIfEmpty(0).Min();
            return lines.Select(line => line.Length >= margin ? line.Substring(margin) : line.TrimStart());
        }

        private static List<object> SourcesFirst(Model model, List<object> nodes)
        {
            var result = new List<object>();
            var seen = new HashSet<object>();
            Action<object> visit = null;
            visit = node =>
            {
                if (!seen.Add(node))
                    return;
                foreach (var slot in model.Inputs(node))
                    foreach (var edge in model.IncomingEdges(node, slot))
                    {
                        var source = Sg.Get(Sg.Get(edge, "outputSlot"), "node");
                        if (nodes.Contains(source))
                            visit(source);
                    }
                result.Add(node);
            };
            foreach (var node in nodes)
                visit(node);
            return result;
        }

        private static bool IsPropertyNode(object node)
        {
            return node.GetType().Name == "PropertyNode" && Sg.Get(node, "property") != null;
        }

        private static bool IsSubGraphOutput(object node)
        {
            return node.GetType().Name == "SubGraphOutputNode";
        }

        // ---------- parse ----------

        private enum TokenKind { Word, Number, Text, Symbol, End }

        private sealed class Token
        {
            internal TokenKind kind;
            internal string text;
            internal int start;
            internal int end;
            internal int line;
        }

        private static readonly Regex NumberToken = new Regex(@"\G(\d+\.?\d*([eE][+-]?\d+)?|\.\d+([eE][+-]?\d+)?)[fFhH]?");
        private static readonly string[] TwoCharSymbols = { "<=", ">=", "==", "!=", "&&", "||", "+=", "-=", "*=", "/=" };

        private static List<Token> Tokenize(string source, int firstLine)
        {
            var tokens = new List<Token>();
            var line = firstLine;
            var index = 0;
            while (index < source.Length)
            {
                var c = source[index];
                if (c == '\n')
                {
                    line++;
                    index++;
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    index++;
                    continue;
                }
                if (c == '/' && index + 1 < source.Length && source[index + 1] == '/')
                {
                    while (index < source.Length && source[index] != '\n')
                        index++;
                    continue;
                }
                if (c == '/' && index + 1 < source.Length && source[index + 1] == '*')
                {
                    var close = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    var stop = close < 0 ? source.Length : close + 2;
                    line += source.Substring(index, stop - index).Count(ch => ch == '\n');
                    index = stop;
                    continue;
                }
                var token = new Token { start = index, line = line };
                if (c == '"')
                {
                    var text = new StringBuilder();
                    index++;
                    while (index < source.Length && source[index] != '"')
                    {
                        if (source[index] == '\\' && index + 1 < source.Length)
                        {
                            index++;
                            text.Append(source[index] == 'n' ? '\n' : source[index]);
                        }
                        else
                            text.Append(source[index]);
                        index++;
                    }
                    if (index >= source.Length)
                        throw new FormatException("line " + line + ": unclosed string");
                    index++;
                    token.kind = TokenKind.Text;
                    token.text = text.ToString();
                }
                else if (char.IsLetterOrDigit(c) || c == '_' || c == '.' && index + 1 < source.Length && char.IsDigit(source[index + 1]))
                {
                    var number = NumberToken.Match(source, index);
                    var after = number.Success ? index + number.Length : index;
                    if (number.Success && number.Length > 0 && (after >= source.Length || !(char.IsLetterOrDigit(source[after]) || source[after] == '_')))
                    {
                        token.kind = TokenKind.Number;
                        token.text = number.Value.TrimEnd('f', 'F', 'h', 'H');
                        index = after;
                    }
                    else
                    {
                        while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] == '_'))
                            index++;
                        token.kind = TokenKind.Word;
                        token.text = source.Substring(token.start, index - token.start);
                        if (token.text.Length == 0)
                        {
                            index++;
                            token.kind = TokenKind.Symbol;
                            token.text = ".";
                        }
                    }
                }
                else
                {
                    var pair = index + 1 < source.Length ? source.Substring(index, 2) : null;
                    token.kind = TokenKind.Symbol;
                    token.text = pair != null && TwoCharSymbols.Contains(pair) ? pair : c.ToString();
                    index += token.text.Length;
                }
                token.end = index;
                tokens.Add(token);
            }
            tokens.Add(new Token { kind = TokenKind.End, text = string.Empty, start = source.Length, end = source.Length, line = line });
            return tokens;
        }

        private abstract class Expr
        {
            internal int line;
        }

        private sealed class NumberExpr : Expr
        {
            internal float value;
        }

        private sealed class TextExpr : Expr
        {
            internal string text;
        }

        private sealed class NameExpr : Expr
        {
            internal string name;
        }

        private sealed class MemberExpr : Expr
        {
            internal Expr target;
            internal string member;
        }

        private sealed class CallExpr : Expr
        {
            internal Expr receiver;
            internal string head;
            internal List<Arg> args = new List<Arg>();
            internal string body;
        }

        private sealed class UnaryExpr : Expr
        {
            internal string op;
            internal Expr operand;
        }

        private sealed class BinaryExpr : Expr
        {
            internal string op;
            internal Expr left;
            internal Expr right;
        }

        private sealed class TernaryExpr : Expr
        {
            internal Expr condition;
            internal Expr whenTrue;
            internal Expr whenFalse;
        }

        // An argument: "Name: value" or positional; settings take the raw text ("Space: World"), inputs the expression.
        private sealed class Arg
        {
            internal string name;
            internal string raw;
            internal Expr value;
            internal string error;
        }

        private sealed class Statement
        {
            internal string target;
            internal bool declared;
            internal Expr value;
            internal string group;
            internal int line;
        }

        private sealed class CodeParser
        {
            private readonly string source;
            private readonly List<Token> tokens;
            private readonly bool hlsl;
            private int position;
            private int limit;

            internal CodeParser(string source, bool hlsl, int firstLine = 1)
            {
                this.source = source;
                this.hlsl = hlsl;
                tokens = Tokenize(source, firstLine);
                limit = tokens.Count - 1;
            }

            private Token Peek(int offset = 0)
            {
                var index = position + offset;
                return index < limit ? tokens[index] : tokens[tokens.Count - 1];
            }

            private bool Is(string symbol, int offset = 0)
            {
                var token = Peek(offset);
                return token.kind == TokenKind.Symbol && token.text == symbol;
            }

            private Token Next()
            {
                var token = Peek();
                if (position < limit)
                    position++;
                return token;
            }

            private void Expect(string symbol)
            {
                if (!Is(symbol))
                    throw Error(Peek(), "expected " + symbol + (Peek().kind == TokenKind.End ? string.Empty : ", found " + Peek().text));
                Next();
            }

            private static FormatException Error(Token token, string message)
            {
                return new FormatException("line " + token.line + ": " + message);
            }

            internal List<Statement> Statements()
            {
                var result = new List<Statement>();
                ParseBlock(null, result);
                if (Peek().kind != TokenKind.End)
                    throw Error(Peek(), "unexpected " + Peek().text);
                return result;
            }

            private void ParseBlock(string group, List<Statement> result)
            {
                while (Peek().kind != TokenKind.End && !Is("}"))
                {
                    if (Is(";"))
                    {
                        Next();
                        continue;
                    }
                    var first = Peek();
                    if (!hlsl && first.kind == TokenKind.Word && first.text == "group" && Peek(1).kind == TokenKind.Text)
                    {
                        Next();
                        var title = Next().text;
                        Expect("{");
                        ParseBlock(title, result);
                        Expect("}");
                        continue;
                    }
                    if (hlsl && first.kind == TokenKind.Word && first.text == "return" && Is(";", 1))
                    {
                        Next();
                        continue;
                    }
                    if (first.kind == TokenKind.Word && new[] { "if", "else", "for", "while", "do", "switch", "return", "struct", "break", "continue", "discard" }.Contains(first.text) ||
                        first.kind == TokenKind.Symbol)
                        throw Error(first, first.text + " is not converted to nodes; keep this code in a Custom Function");
                    var statement = ParseStatement(group);
                    if (statement != null)
                        result.Add(statement);
                }
            }

            private Statement ParseStatement(string group)
            {
                var first = Peek();
                // The target is the text up to "=": an id, a name, "Base Color", "Normal (Tangent Space)", "7c6c.B".
                var parts = new List<Token>();
                while (Peek().kind != TokenKind.End && Peek().line == first.line && !Is(";") && !Is("{") && !Is("}") &&
                    !(Peek().kind == TokenKind.Symbol && new[] { "=", "+=", "-=", "*=", "/=" }.Contains(Peek().text)))
                    parts.Add(Next());
                if (parts.Count > 0 && parts[0].kind == TokenKind.Word && parts[0].text == "const")
                    parts.RemoveAt(0);
                // "float a, b;" only declares; the variables get their values from later lines.
                if (hlsl && parts.Count >= 2 && parts[0].kind == TokenKind.Word && HlslType.IsMatch(parts[0].text) && Is(";"))
                {
                    Next();
                    return null;
                }
                var declared = false;
                if (parts.Count == 2 && parts[0].kind == TokenKind.Word && HlslType.IsMatch(parts[0].text) && parts[1].kind == TokenKind.Word)
                {
                    parts.RemoveAt(0);
                    declared = true;
                }
                var op = Peek();
                if (parts.Count == 0 || op.kind != TokenKind.Symbol || !new[] { "=", "+=", "-=", "*=", "/=" }.Contains(op.text))
                    throw Error(op, "expected <target> = <expression>" + (op.kind == TokenKind.End ? string.Empty : ", found " + op.text));
                Next();
                var target = new StringBuilder(Regex.Replace(source.Substring(parts[0].start, parts[parts.Count - 1].end - parts[0].start), @"\s+", " "));
                var value = ParseExpression();
                if (op.text != "=")
                    value = new BinaryExpr { op = op.text.Substring(0, 1), left = new NameExpr { name = target.ToString(), line = op.line }, right = value, line = op.line };
                if (Is(";"))
                    Next();
                return new Statement { target = target.ToString(), declared = declared, value = value, group = group, line = first.line };
            }

            internal Expr ParseExpression()
            {
                var condition = ParseBinary(1);
                if (!Is("?"))
                    return condition;
                var token = Next();
                var whenTrue = ParseExpression();
                Expect(":");
                return new TernaryExpr { condition = condition, whenTrue = whenTrue, whenFalse = ParseExpression(), line = token.line };
            }

            private static int Precedence(string op)
            {
                switch (op)
                {
                    case "||": return 1;
                    case "&&": return 2;
                    case "==": case "!=": return 3;
                    case "<": case ">": case "<=": case ">=": return 4;
                    case "+": case "-": return 5;
                    case "*": case "/": case "%": return 6;
                    default: return 0;
                }
            }

            private Expr ParseBinary(int minimum)
            {
                var left = ParseUnary();
                while (Peek().kind == TokenKind.Symbol && Precedence(Peek().text) >= minimum)
                {
                    var token = Next();
                    left = new BinaryExpr { op = token.text, left = left, right = ParseBinary(Precedence(token.text) + 1), line = token.line };
                }
                return left;
            }

            private Expr ParseUnary()
            {
                if (Is("-") || Is("!") || Is("+"))
                {
                    var token = Next();
                    var operand = ParseUnary();
                    return token.text == "+" ? operand : new UnaryExpr { op = token.text, operand = operand, line = token.line };
                }
                return ParsePostfix();
            }

            private Expr ParsePostfix()
            {
                var expr = ParsePrimary();
                while (Is(".") && Peek(1).kind == TokenKind.Word)
                {
                    Next();
                    var member = Next();
                    if (Is("(") && Peek().line == member.line)
                    {
                        var call = new CallExpr { receiver = expr, head = member.text, line = member.line };
                        ParseArgs(call);
                        expr = call;
                    }
                    else
                        expr = new MemberExpr { target = expr, member = member.text, line = member.line };
                }
                return expr;
            }

            private Expr ParsePrimary()
            {
                var token = Peek();
                switch (token.kind)
                {
                    case TokenKind.Number:
                        Next();
                        return new NumberExpr { value = float.Parse(token.text, NumberStyles.Float, CultureInfo.InvariantCulture), line = token.line };
                    case TokenKind.Text:
                        Next();
                        return new TextExpr { text = token.text, line = token.line };
                    case TokenKind.Word:
                        // A node title may have several words on one line: "Sample Texture 2D(...)".
                        var count = 1;
                        while ((Peek(count).kind == TokenKind.Word || Peek(count).kind == TokenKind.Number) && Peek(count).line == token.line)
                            count++;
                        if (Is("(", count) && Peek(count).line == token.line)
                        {
                            var head = string.Join(" ", Enumerable.Range(0, count).Select(index => Peek(index).text));
                            position += count;
                            var call = new CallExpr { head = head, line = token.line };
                            ParseArgs(call);
                            if (Is("{"))
                                call.body = ParseBody();
                            return call;
                        }
                        Next();
                        return new NameExpr { name = token.text, line = token.line };
                }
                if (Is("("))
                {
                    Next();
                    var inner = ParseExpression();
                    Expect(")");
                    return inner;
                }
                throw Error(token, token.kind == TokenKind.End ? "expression expected" : "unexpected " + token.text);
            }

            private void ParseArgs(CallExpr call)
            {
                Expect("(");
                while (!Is(")"))
                {
                    var arg = new Arg();
                    var count = 0;
                    while ((Peek(count).kind == TokenKind.Word || Peek(count).kind == TokenKind.Number) && Peek(count).line == Peek().line)
                        count++;
                    if (count > 0 && Is(":", count))
                    {
                        arg.name = string.Join(" ", Enumerable.Range(0, count).Select(index => Peek(index).text));
                        position += count + 1;
                    }
                    var from = position;
                    var depth = 0;
                    while (Peek().kind != TokenKind.End && (depth > 0 || !Is(",") && !Is(")")))
                    {
                        if (Is("(") || Is("{"))
                            depth++;
                        if (Is(")") || Is("}"))
                            depth--;
                        Next();
                    }
                    var to = position;
                    if (to == from)
                        throw Error(Peek(), "argument value expected");
                    arg.raw = to - from == 1 && tokens[from].kind == TokenKind.Text ? tokens[from].text : source.Substring(tokens[from].start, tokens[to - 1].end - tokens[from].start).Trim();
                    var savedLimit = limit;
                    position = from;
                    limit = to;
                    try
                    {
                        arg.value = ParseExpression();
                        if (position != to)
                            throw Error(Peek(), "unexpected " + Peek().text);
                    }
                    catch (FormatException error)
                    {
                        arg.value = null;
                        arg.error = error.Message;
                    }
                    limit = savedLimit;
                    position = to;
                    call.args.Add(arg);
                    if (Is(","))
                        Next();
                    else if (!Is(")"))
                        throw Error(Peek(), "expected , or )");
                }
                Expect(")");
            }

            // The code between braces, as written: a Custom Function body.
            private string ParseBody()
            {
                var open = Next();
                var depth = 1;
                while (Peek().kind != TokenKind.End)
                {
                    if (Is("{"))
                        depth++;
                    if (Is("}") && --depth == 0)
                        break;
                    Next();
                }
                var close = Peek();
                Expect("}");
                return string.Join("\n", Dedent(source.Substring(open.end, close.start - open.end)));
            }
        }

        // ---------- write ----------

        private sealed class CodeValue
        {
            internal object node;
            internal object slot;
            internal float[] numbers;
            internal string word;
            // A Blackboard property: an input already fed by it keeps its node, otherwise a node is dragged in.
            internal object property;
        }

        // HLSL intrinsics and the nodes they are: arguments in the node's input order, plus settings the name implies.
        private static readonly Dictionary<string, string> Intrinsics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "lerp", "Lerp" }, { "saturate", "Saturate" }, { "clamp", "Clamp" }, { "step", "Step" }, { "smoothstep", "Smoothstep" },
            { "pow", "Power" }, { "sqrt", "Square Root" }, { "rsqrt", "Reciprocal Square Root" }, { "rcp", "Reciprocal" }, { "abs", "Absolute" },
            { "exp", "Exponential" }, { "exp2", "Exponential|Base=Base2" }, { "log", "Log" }, { "log2", "Log|Base=Base2" }, { "log10", "Log|Base=Base10" },
            { "floor", "Floor" }, { "ceil", "Ceiling" }, { "round", "Round" }, { "frac", "Fraction" }, { "sign", "Sign" }, { "trunc", "Truncate" },
            { "fmod", "Modulo" }, { "min", "Minimum" }, { "max", "Maximum" }, { "sin", "Sine" }, { "cos", "Cosine" }, { "tan", "Tangent" },
            { "asin", "Arcsine" }, { "acos", "Arccosine" }, { "atan", "Arctangent" }, { "atan2", "Arctangent2" }, { "sinh", "Hyperbolic Sine" },
            { "cosh", "Hyperbolic Cosine" }, { "tanh", "Hyperbolic Tangent" }, { "dot", "Dot Product" }, { "cross", "Cross Product" },
            { "normalize", "Normalize" }, { "length", "Length" }, { "distance", "Distance" }, { "reflect", "Reflection" }, { "ddx", "DDX" },
            { "ddy", "DDY" }, { "fwidth", "DDXY" }, { "radians", "Degrees To Radians" }, { "degrees", "Radians To Degrees" }, { "mul", "Multiply" },
        };

        private static readonly Dictionary<string, string> Operators = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "+", "Add" }, { "-", "Subtract" }, { "*", "Multiply" }, { "/", "Divide" }, { "%", "Modulo" }, { "&&", "And" }, { "||", "Or" },
            { "==", "Comparison|Comparison Type=Equal" }, { "!=", "Comparison|Comparison Type=NotEqual" }, { "<", "Comparison|Comparison Type=Less" },
            { "<=", "Comparison|Comparison Type=LessOrEqual" }, { ">", "Comparison|Comparison Type=Greater" }, { ">=", "Comparison|Comparison Type=GreaterOrEqual" },
        };

        // A node an expression stands for: its Create Node title, its arguments and the settings its name implies.
        private sealed class Shape
        {
            internal string title;
            internal List<Arg> args = new List<Arg>();
            internal List<KeyValuePair<string, string>> presets = new List<KeyValuePair<string, string>>();
            internal string body;
            // An HLSL function takes exactly its arguments, as HLSL would: lerp(a, b) is an error, not a Lerp with T left at 0.
            internal string hlsl;
        }

        private static Shape ShapeOf(string spec, IEnumerable<Expr> positional)
        {
            var parts = spec.Split('|');
            var shape = new Shape { title = parts[0] };
            foreach (var preset in parts.Skip(1))
                shape.presets.Add(new KeyValuePair<string, string>(preset.Split('=')[0], preset.Split('=')[1]));
            shape.args.AddRange(positional.Select(expr => new Arg { value = expr }));
            return shape;
        }

        private static Arg Named(string name, Expr value)
        {
            return new Arg { name = name, value = value };
        }

        private sealed class CodeWriter
        {
            private readonly EditorWindow window;
            private readonly object graph;
            private readonly bool hlsl;
            private readonly List<object> entries;
            private readonly Dictionary<string, CodeValue> variables = new Dictionary<string, CodeValue>(StringComparer.Ordinal);
            private readonly Dictionary<string, Statement> pending = new Dictionary<string, Statement>(StringComparer.Ordinal);
            private readonly HashSet<Statement> done = new HashSet<Statement>();
            private readonly HashSet<object> claimed = new HashSet<object>();
            private readonly List<object> writtenGroups = new List<object>();
            internal readonly List<object> created = new List<object>();
            internal readonly List<object> touched = new List<object>();
            internal readonly List<string> removed = new List<string>();
            internal readonly List<string> replaced = new List<string>();
            internal readonly List<string> names = new List<string>();
            private readonly List<KeyValuePair<object, object>> typed = new List<KeyValuePair<object, object>>();
            private object group;

            internal CodeWriter(EditorWindow window, object graph, bool hlsl)
            {
                this.window = window;
                this.graph = graph;
                this.hlsl = hlsl;
                entries = NodeEntries(window).ToList();
            }

            private Model Model { get { return new Model(graph); } }

            internal void Bind(string name, CodeValue value)
            {
                variables[name] = value;
            }

            internal CodeValue Variable(string name)
            {
                CodeValue value;
                return variables.TryGetValue(name, out value) ? value : null;
            }

            internal void SetGroup(object value)
            {
                group = value;
            }

            // A typed vector of a Custom Function input stays a vector: a Vector node, not a value a Float port would cut.
            internal CodeValue Constant(float[] numbers, int size)
            {
                var node = Create(size == 1 ? "Float" : "Vector " + size);
                var inputs = Model.Inputs(node);
                for (var index = 0; index < inputs.Count; index++)
                    SetInput(node, inputs[index], new CodeValue { numbers = new[] { Component(numbers, index) } });
                return new CodeValue { node = node };
            }

            internal void RemoveUnused(IEnumerable<object> nodes)
            {
                var model = Model;
                var unused = nodes.Where(node => model.nodes.Contains(node) && !model.Outputs(node).Any(output => Edges(node, output).Any())).ToList();
                if (unused.Count > 0)
                    Remove(unused);
            }

            internal void Run(List<Statement> statements)
            {
                foreach (var statement in statements.Where(item => IsVariable(item)))
                    if (!pending.ContainsKey(statement.target))
                        pending[statement.target] = statement;
                foreach (var statement in statements)
                    Process(statement);
                KeepVectors();
                // A written group holds what its block says: members the text neither writes nor uses are deleted.
                var model = Model;
                var doomed = model.nodes.Where(node => writtenGroups.Contains(Sg.Get(node, "group")) && !claimed.Contains(node) && !model.IsBlock(node)).ToList();
                if (doomed.Count > 0)
                    Remove(doomed);
            }

            private bool IsVariable(Statement statement)
            {
                if (hlsl)
                    return true;
                if (statement.declared)
                    return PlainWord.IsMatch(statement.target);
                // Blocks are printed by their labels, capitalized ("Alpha = ..."); a lower-case word ("mask") is a variable.
                return PlainWord.IsMatch(statement.target) && ExactNode(statement.target) == null &&
                    (char.IsLower(statement.target[0]) || Block(statement.target) == null && !KnownBlock(statement.target));
            }

            private void Process(Statement statement)
            {
                if (!done.Add(statement))
                    return;
                var previous = group;
                if (!hlsl)
                    group = statement.group == null ? null : Group(statement.group);
                try
                {
                    Apply(statement);
                }
                catch (Exception error) when (!error.Message.StartsWith("line ", StringComparison.Ordinal))
                {
                    throw new ArgumentException("line " + statement.line + ": " + error.Message, error);
                }
                finally
                {
                    group = previous;
                }
            }

            private void Apply(Statement statement)
            {
                var target = statement.target;
                if (hlsl && !PlainWord.IsMatch(target))
                    throw new ArgumentException(target + ": assigning part of a vector is not converted to nodes; keep this code in a Custom Function");
                if (IsVariable(statement))
                {
                    var value = Evaluate(statement.value);
                    variables[target] = value;
                    names.RemoveAll(item => item.StartsWith(target + "=", StringComparison.Ordinal));
                    if (value.node != null && created.Contains(value.node))
                        names.Add(target + "=" + Model.Id(value.node));
                    return;
                }
                var node = ExactNode(target);
                if (node != null)
                {
                    Rewrite(node, statement.value);
                    return;
                }
                var block = Block(target) ?? NewBlock(target);
                if (block != null)
                {
                    SetInput(block, Model.Inputs(block).First(), Evaluate(statement.value));
                    return;
                }
                var dot = target.LastIndexOf('.');
                if (dot > 0)
                {
                    var owner = Variable(target.Substring(0, dot))?.node ?? ExactNode(target.Substring(0, dot)) ?? SubGraphOutput(target.Substring(0, dot));
                    if (owner != null)
                    {
                        var slot = Model.Inputs(owner).FirstOrDefault(item => AssetViews.KeyIs(target.Substring(dot + 1), SlotName(item)));
                        if (slot == null)
                            throw new ArgumentException(Model.Label(owner) + " inputs: " + string.Join(", ", Model.Inputs(owner).Select(SlotName)));
                        SetInput(owner, slot, Evaluate(statement.value));
                        Claim(owner);
                        return;
                    }
                }
                throw new ArgumentException("Not a node id, block or input: " + target);
            }

            // ---------- targets ----------

            // Ids as asset-info and the code print them; a prefix is not enough, so a new name never hits a node by chance.
            private object ExactNode(string key)
            {
                var model = Model;
                return model.nodes.FirstOrDefault(node => !model.IsBlock(node) && string.Equals(model.Id(node), key, StringComparison.OrdinalIgnoreCase));
            }

            private object Block(string key)
            {
                var model = Model;
                var name = StageBlockName(key);
                return model.nodes.FirstOrDefault(node => model.IsBlock(node) && AssetViews.KeyIs(name, model.Label(node), (string)Sg.Get(node, "name")));
            }

            private static string StageBlockName(string key)
            {
                foreach (var stage in new[] { "Vertex.", "Fragment." })
                    if (key.StartsWith(stage, StringComparison.OrdinalIgnoreCase))
                        return key.Substring(stage.Length);
                return key;
            }

            // A block the stack does not show yet is added, as the developer would add it before wiring it.
            private object NewBlock(string key)
            {
                return KnownBlock(key) && !hlsl ? AddBlock(graph, StageBlockName(key)) : null;
            }

            private bool KnownBlock(string key)
            {
                var name = StageBlockName(key);
                return Sg.Items(Sg.Get(graph, "blockFieldDescriptors")).Any(item => !(bool)Sg.Get(item, "isHidden") &&
                    AssetViews.KeyIs(name, (string)Sg.Get(item, "displayName"), (string)Sg.Get(item, "name")));
            }

            private object SubGraphOutput(string key)
            {
                return Model.nodes.FirstOrDefault(node => IsSubGraphOutput(node) && AssetViews.KeyIs(key, (string)Sg.Get(node, "name")));
            }

            private object Group(string title)
            {
                var found = FindGroup(graph, title);
                if (found == null)
                {
                    found = Activator.CreateInstance(Sg.Type("UnityEditor.ShaderGraph.GroupData"), title, Vector2.zero);
                    Undo(graph, "Create Group Node");
                    Sg.Call(graph, "CreateGroup", found);
                }
                if (!writtenGroups.Contains(found))
                    writtenGroups.Add(found);
                return found;
            }

            private void Claim(object node)
            {
                claimed.Add(node);
            }

            private void Join(object node)
            {
                Claim(node);
                if (group != null && Sg.Get(node, "group") != group)
                    Sg.Call(graph, "SetGroup", node, group);
            }

            // ---------- nodes ----------

            private object EntryFor(string title)
            {
                var matches = entries.Where(entry => AssetViews.KeyIs(title, EntryTitle(entry))).ToList();
                if (matches.Count == 0)
                    matches = entries.Where(entry => AssetViews.KeyIs(title, ((string[])Sg.Get(entry, "title")).Last())).ToList();
                if (matches.Count != 1)
                    throw new ArgumentException((matches.Count == 0 ? "Node was not found: " : "Node name is ambiguous: ") + title +
                        ". Search with: creation-templates --path <graph> --query \"...\"" + (matches.Count > 1 ? " Matches: " + string.Join(", ", matches.Select(EntryTitle)) : string.Empty));
                return matches[0];
            }

            private object Create(string title)
            {
                var provider = Sg.Get(ShaderGraphWindow.EditorView(window), "m_SearchWindowProvider");
                var node = Sg.Call(provider, "CopyNodeForGraph", Sg.Get(EntryFor(title), "node"));
                PlaceAndAdd(Model, node, Vector2.zero);
                created.Add(node);
                touched.Add(node);
                Join(node);
                return node;
            }

            private bool SameKind(object node, string title)
            {
                var template = Sg.Get(EntryFor(title), "node");
                if (template.GetType() != node.GetType())
                    return false;
                return node.GetType().Name != "SubGraphNode" || Sg.Get(template, "asset") == Sg.Get(node, "asset");
            }

            private void Remove(List<object> nodes, bool report = true)
            {
                var model = Model;
                if (report)
                    removed.AddRange(nodes.Where(node => !created.Contains(node)).Select(model.Label));
                created.RemoveAll(nodes.Contains);
                Undo(graph, "Delete Nodes");
                Sg.Call(graph, "RemoveElements", Sg.Array("UnityEditor.ShaderGraph.AbstractMaterialNode", nodes), Sg.Array("UnityEditor.Graphing.IEdge", new object[0]),
                    Sg.Array("UnityEditor.ShaderGraph.GroupData", new object[0]), Sg.Array("UnityEditor.ShaderGraph.StickyNoteData", new object[0]), null);
            }

            // A line of an existing node: the same kind of node is set as written (inputs left out are disconnected);
            // another expression takes its place, its wires moved to what replaces it.
            private void Rewrite(object node, Expr expr)
            {
                Join(node);
                var shape = ShapeFor(expr);
                if (shape != null && (AssetViews.KeyIs(shape.title, (string)Sg.Get(node, "name")) || SameKind(node, shape.title)))
                {
                    ApplyShape(node, shape, true);
                    return;
                }
                var constant = new[] { "Vector1Node", "Vector2Node", "Vector3Node", "Vector4Node" }.Contains(node.GetType().Name);
                var value = Evaluate(expr);
                if (constant && value.numbers != null)
                {
                    var inputs = Model.Inputs(node);
                    for (var index = 0; index < inputs.Count; index++)
                        SetInput(node, inputs[index], new CodeValue { numbers = new[] { value.numbers.Length == 1 ? value.numbers[0] : index < value.numbers.Length ? value.numbers[index] : 0f } });
                    return;
                }
                if (value.node == node)
                    return;
                var model = Model;
                var label = model.Label(node);
                foreach (var output in model.Outputs(node))
                    foreach (var edge in Edges(node, output))
                    {
                        var input = Sg.Get(edge, "inputSlot");
                        var target = Sg.Get(input, "node");
                        var slot = model.Inputs(target).FirstOrDefault(item => (int)Sg.Get(item, "id") == (int)Sg.Get(input, "slotId"));
                        if (slot == null)
                            continue;
                        var source = value;
                        if (value.node != null && value.slot == null)
                        {
                            var same = model.Outputs(value.node).FirstOrDefault(item => SlotName(item) == SlotName(output));
                            source = new CodeValue { node = value.node, slot = same };
                        }
                        SetInput(target, slot, source);
                    }
                Remove(new List<object> { node }, false);
                replaced.Add(label + " → " + (value.node != null ? Model.Label(value.node) : value.property != null ? (string)Sg.Get(value.property, "referenceName") : Describe(value)));
            }

            private IEnumerable<object> Edges(object node, object output)
            {
                var id = (int)Sg.Get(output, "id");
                return Sg.Items(Sg.Get(graph, "edges")).Where(edge =>
                {
                    var from = Sg.Get(edge, "outputSlot");
                    return Sg.Get(from, "node") == node && (int)Sg.Get(from, "slotId") == id;
                }).ToList();
            }

            private static string Describe(CodeValue value)
            {
                return value.numbers != null ? AssetViews.Numbers(value.numbers) : value.word;
            }

            // The node an expression is, or null for a reference or value.
            private Shape ShapeFor(Expr expr)
            {
                var call = expr as CallExpr;
                if (call != null)
                {
                    if (call.receiver != null)
                    {
                        if (call.head == "Sample" && call.args.Count >= 2)
                            return new Shape { title = "Sample Texture 2D", args = { Named("Texture", call.receiver), Named("UV", call.args[1].value) } };
                        if (call.head == "SampleLevel" && call.args.Count >= 3)
                            return new Shape { title = "Sample Texture 2D LOD", args = { Named("Texture", call.receiver), Named("UV", call.args[1].value), Named("LOD", call.args[2].value) } };
                        throw new ArgumentException(call.head + "() is not a node; methods: Sample, SampleLevel");
                    }
                    if (call.head == "tex2D" && call.args.Count == 2)
                        return new Shape { title = "Sample Texture 2D", args = { Named("Texture", call.args[0].value), Named("UV", call.args[1].value) } };
                    if (call.head == "SAMPLE_TEXTURE2D" && call.args.Count == 3)
                        return new Shape { title = "Sample Texture 2D", args = { Named("Texture", call.args[0].value), Named("UV", call.args[2].value) } };
                    if (call.head == "SAMPLE_TEXTURE2D_LOD" && call.args.Count == 4)
                        return new Shape { title = "Sample Texture 2D LOD", args = { Named("Texture", call.args[0].value), Named("UV", call.args[2].value), Named("LOD", call.args[3].value) } };
                    if (HlslType.IsMatch(call.head))
                        return null;
                    string spec;
                    if (Intrinsics.TryGetValue(call.head, out spec))
                    {
                        if (call.args.Any(arg => arg.name != null))
                            throw new ArgumentException(call.head + "() takes positional arguments");
                        var intrinsic = ShapeOf(spec, call.args.Select(arg => arg.value));
                        intrinsic.hlsl = call.head;
                        return intrinsic;
                    }
                    return new Shape { title = call.head, args = call.args, body = call.body };
                }
                var binary = expr as BinaryExpr;
                if (binary != null)
                    return Operators.ContainsKey(binary.op) ? ShapeOf(Operators[binary.op], new[] { binary.left, binary.right }) : null;
                var unary = expr as UnaryExpr;
                if (unary != null)
                    return ShapeOf(unary.op == "-" ? "Negate" : "Not", new[] { unary.operand });
                var ternary = expr as TernaryExpr;
                if (ternary != null)
                    return ShapeOf("Branch", new[] { ternary.condition, ternary.whenTrue, ternary.whenFalse });
                return null;
            }

            // Settings first (they decide the slots: Custom Function lists, Swizzle mask), then inputs by name or in order.
            private void ApplyShape(object node, Shape shape, bool whole)
            {
                var id = Model.Id(node);
                var custom = node.GetType().Name == "CustomFunctionNode";
                var settings = new List<KeyValuePair<string, string>>(shape.presets);
                var inputs = new List<Arg>();
                foreach (var arg in shape.args)
                {
                    var control = arg.name == null ? (Control?)null : Controls(node).Cast<Control?>().FirstOrDefault(item => AssetViews.KeyIs(arg.name, item.Value.label, item.Value.property.Name));
                    if (control != null || custom && arg.name != null && AssetViews.KeyIs(arg.name, "Inputs", "Outputs"))
                        settings.Add(new KeyValuePair<string, string>(control == null ? arg.name : control.Value.label, arg.raw));
                    else
                        inputs.Add(arg);
                }
                if (shape.body != null)
                    settings.Add(new KeyValuePair<string, string>("Body", shape.body));
                if (custom && shape.body != null && !settings.Any(item => AssetViews.KeyIs(item.Key, "Type")))
                    settings.Insert(0, new KeyValuePair<string, string>("Type", "String"));
                foreach (var setting in settings.OrderBy(item => AssetViews.KeyIs(item.Key, "Type") ? 0 : AssetViews.KeyIs(item.Key, "Inputs", "Outputs") ? 1 : 2))
                {
                    // What the node already shows is left alone, so a line written back unchanged changes nothing.
                    var current = Setting(node, setting.Key);
                    if (current != null && (AssetViews.KeyIs(setting.Key, "Body") ? current == setting.Value : current == setting.Value || AssetViews.KeyIs(current, setting.Value)))
                        continue;
                    SetNodeField(window, Model, id + "." + setting.Key, setting.Value, touched);
                    touched.Add(node);
                }
                var slots = Model.Inputs(node);
                if (shape.hlsl != null && inputs.Count != slots.Count)
                    throw new ArgumentException(shape.hlsl + "() takes " + slots.Count + " arguments (" + string.Join(", ", slots.Select(SlotName)) + "), got " + inputs.Count);
                var assigned = new HashSet<object>();
                var free = new Queue<object>(slots);
                foreach (var arg in inputs)
                {
                    object slot;
                    if (arg.name != null)
                    {
                        slot = slots.FirstOrDefault(item => AssetViews.KeyIs(arg.name, SlotName(item), Sg.Get(item, "id").ToString()));
                        if (slot == null)
                            throw new ArgumentException(Model.Label(node) + " inputs: " + string.Join(", ", slots.Select(SlotName)) +
                                "; settings: " + string.Join(", ", Controls(node).Select(item => item.label).Concat(custom ? new[] { "Inputs", "Outputs" } : new string[0])));
                    }
                    else
                    {
                        while (free.Count > 0 && assigned.Contains(free.Peek()))
                            free.Dequeue();
                        if (free.Count == 0)
                            throw new ArgumentException("Too many arguments for " + Model.Label(node) + "; inputs: " + string.Join(", ", slots.Select(SlotName)));
                        slot = free.Dequeue();
                    }
                    assigned.Add(slot);
                    if (arg.value == null && arg.error != null && arg.name == null)
                        throw new FormatException(arg.error);
                    SetInput(node, slot, arg.value == null ? new CodeValue { word = arg.raw } : Evaluate(arg.value));
                }
                if (!whole)
                    return;
                var model = Model;
                foreach (var slot in model.Inputs(node).Where(item => !assigned.Contains(item) && model.IncomingEdges(node, item).Any()))
                {
                    Disconnect(graph, model.IncomingEdges(node, slot));
                    touched.Add(node);
                }
            }

            private string Setting(object node, string key)
            {
                var model = Model;
                if (AssetViews.KeyIs(key, "Inputs"))
                    return SlotList(model.Inputs(node));
                if (AssetViews.KeyIs(key, "Outputs"))
                    return SlotList(model.Outputs(node));
                if (AssetViews.KeyIs(key, "Body"))
                    return node.GetType().Name == "CustomFunctionNode" ? string.Join("\n", Dedent((string)Sg.Get(node, "functionBody"))) : null;
                var control = Controls(node).Cast<Control?>().FirstOrDefault(item => AssetViews.KeyIs(key, item.Value.label, item.Value.property.Name));
                var value = control == null ? null : ControlValue(node, control.Value);
                return value == null ? null : value is bool ? ((bool)value ? "true" : "false") : Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            internal void SetInput(object node, object slot, CodeValue value)
            {
                var model = Model;
                var edge = model.IncomingEdges(node, slot).FirstOrDefault();
                var from = edge == null ? null : Sg.Get(edge, "outputSlot");
                var current = from == null ? null : Sg.Get(from, "node");
                if (value.property != null)
                {
                    if (current != null && IsPropertyNode(current) && Sg.Get(current, "property") == value.property)
                    {
                        Claim(current);
                        return;
                    }
                    value = new CodeValue { node = PropertyNode(value.property) };
                }
                if (value.node != null)
                {
                    var output = value.slot ?? model.Outputs(value.node).FirstOrDefault();
                    if (output == null)
                        throw new ArgumentException(model.Label(value.node) + " has no output.");
                    if (current == value.node && (int)Sg.Get(from, "slotId") == (int)Sg.Get(output, "id"))
                    {
                        Claim(value.node);
                        return;
                    }
                    if (!(bool)Sg.Call(output, "IsCompatibleWith", slot))
                        throw new ArgumentException(model.Label(value.node) + "." + SlotName(output) + " (" + SlotType(output) + ") cannot connect to " +
                            model.Label(node) + "." + SlotName(slot) + " (" + SlotType(slot) + ").");
                    Connect(graph, value.node, output, node, slot);
                    Claim(value.node);
                    touched.Add(node);
                    return;
                }
                var text = value.numbers != null ? AssetViews.Numbers(value.numbers) : value.word;
                if (edge == null && SameValue(SlotValue(slot) as string, text))
                    return;
                Disconnect(graph, model.IncomingEdges(node, slot));
                SetSlotValue(graph, node, slot, text);
                touched.Add(node);
                if (value.numbers != null && value.numbers.Length > 1)
                    typed.Add(new KeyValuePair<object, object>(node, slot));
            }

            // HLSL's lerp(float3(...), float3(...), mask) is a float3; a dynamic node takes its size from wires only, so a
            // typed vector it would cut to Float becomes a Vector node, as the developer would place one.
            private void KeepVectors()
            {
                if (typed.Count == 0)
                    return;
                Sg.Call(graph, "ValidateGraph");
                var previous = group;
                foreach (var pair in typed)
                {
                    var model = Model;
                    var node = pair.Key;
                    var slot = pair.Value;
                    if (!model.nodes.Contains(node) || model.IncomingEdges(node, slot).Any() || !slot.GetType().Name.StartsWith("Dynamic", StringComparison.Ordinal))
                        continue;
                    var row = Sg.Get(slot, "value");
                    if (!(row is Matrix4x4) && !(row is Vector4))
                        continue;
                    var vector = row is Matrix4x4 ? ((Matrix4x4)row).GetRow(0) : (Vector4)row;
                    var size = Enumerable.Range(0, 4).Last(index => index == 0 || Mathf.Abs(vector[index]) > 1e-6f) + 1;
                    var uniform = Enumerable.Range(1, 3).All(index => Mathf.Abs(vector[index] - vector[0]) <= 1e-6f);
                    if (uniform || VectorSize(slot) >= size)
                        continue;
                    group = Sg.Get(node, "group");
                    var constant = Constant(Enumerable.Range(0, size).Select(index => vector[index]).ToArray(), Math.Max(2, size));
                    group = previous;
                    SetInput(node, slot, constant);
                }
                group = previous;
            }

            private static bool SameValue(string current, string text)
            {
                if (current == null || text == null)
                    return false;
                var a = current.Split(',');
                var b = text.Split(',');
                float x, y;
                if (a.Length == b.Length && a.Zip(b, (left, right) => float.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                        float.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out y) && Mathf.Abs(x - y) < 1e-5f).All(same => same))
                    return true;
                return AssetViews.KeyIs(current, text) && !a.Concat(b).Any(part => float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out x));
            }

            // A property where a node is needed (a swizzle, a Combine component).
            private CodeValue Concrete(CodeValue value)
            {
                return value.property == null ? value : new CodeValue { node = PropertyNode(value.property) };
            }

            // ---------- expressions ----------

            private CodeValue Evaluate(Expr expr)
            {
                var number = expr as NumberExpr;
                if (number != null)
                    return new CodeValue { numbers = new[] { number.value } };
                var text = expr as TextExpr;
                if (text != null)
                    return new CodeValue { word = text.text };
                var name = expr as NameExpr;
                if (name != null)
                    return Resolve(name.name);
                var member = expr as MemberExpr;
                if (member != null)
                    return Member(Evaluate(member.target), member.member);
                var call = expr as CallExpr;
                if (call != null && call.receiver == null && HlslType.IsMatch(call.head))
                    return Construct(call);
                if (call != null && call.head == "mad" && call.args.Count == 3)
                    return Evaluate(new BinaryExpr { op = "+", left = new BinaryExpr { op = "*", left = call.args[0].value, right = call.args[1].value }, right = call.args[2].value });
                var unary = expr as UnaryExpr;
                if (unary != null && unary.op == "-")
                {
                    var operand = Evaluate(unary.operand);
                    if (operand.numbers != null)
                        return new CodeValue { numbers = operand.numbers.Select(item => -item).ToArray() };
                    return Node(ShapeOf("Negate", new Expr[0]), new[] { operand });
                }
                var binary = expr as BinaryExpr;
                if (binary != null && "+-*/".Contains(binary.op))
                {
                    var left = Evaluate(binary.left);
                    var right = Evaluate(binary.right);
                    if (left.numbers != null && right.numbers != null)
                        return Fold(binary.op, left.numbers, right.numbers);
                    return Node(ShapeOf(Operators[binary.op], new Expr[0]), new[] { left, right });
                }
                var shape = ShapeFor(expr);
                if (shape == null)
                    throw new ArgumentException("Not an expression the graph has nodes for.");
                var node = Create(shape.title);
                ApplyShape(node, shape, false);
                return new CodeValue { node = node };
            }

            // A node whose inputs are values already worked out (folded operators keep their operands' nodes).
            private CodeValue Node(Shape shape, IList<CodeValue> inputs)
            {
                var node = Create(shape.title);
                ApplyShape(node, shape, false);
                var slots = Model.Inputs(node);
                for (var index = 0; index < inputs.Count && index < slots.Count; index++)
                    SetInput(node, slots[index], inputs[index]);
                return new CodeValue { node = node };
            }

            private static CodeValue Fold(string op, float[] left, float[] right)
            {
                var size = Math.Max(left.Length, right.Length);
                Func<float[], int, float> at = (numbers, index) => numbers.Length == 1 ? numbers[0] : index < numbers.Length ? numbers[index] : 0f;
                return new CodeValue
                {
                    numbers = Enumerable.Range(0, size).Select(index =>
                    {
                        var a = at(left, index);
                        var b = at(right, index);
                        return op == "+" ? a + b : op == "-" ? a - b : op == "*" ? a * b : a / b;
                    }).ToArray()
                };
            }

            private CodeValue Resolve(string name)
            {
                CodeValue value;
                if (variables.TryGetValue(name, out value))
                    return value;
                Statement later;
                if (pending.TryGetValue(name, out later) && !done.Contains(later))
                {
                    Process(later);
                    if (variables.TryGetValue(name, out value))
                        return value;
                }
                if (hlsl)
                    throw new ArgumentException(name + " is not a parameter or a variable of the function.");
                var node = ExactNode(name);
                if (node != null)
                {
                    Claim(node);
                    return new CodeValue { node = node };
                }
                var input = FindInput(graph, name, false);
                if (input != null)
                    return new CodeValue { property = input };
                return new CodeValue { word = name };
            }

            // A Blackboard property dragged in once per group: its node there is reused, else a new one joins the group.
            private object PropertyNode(object input)
            {
                var node = Model.nodes.FirstOrDefault(item => IsPropertyNode(item) && Sg.Get(item, "property") == input && Sg.Get(item, "group") == group);
                if (node != null)
                {
                    Claim(node);
                    return node;
                }
                return Create("Properties/Property: " + Sg.Get(input, "displayName"));
            }

            private CodeValue Member(CodeValue value, string member)
            {
                value = Concrete(value);
                if (value.node != null && value.slot == null)
                {
                    var slot = Model.Outputs(value.node).FirstOrDefault(item => AssetViews.KeyIs(member, SlotName(item)));
                    if (slot != null)
                        return new CodeValue { node = value.node, slot = slot };
                }
                if (!Swizzle.IsMatch(member))
                    throw new ArgumentException(member + " is not an output" + (value.node == null ? string.Empty : " of " + Model.Label(value.node) +
                        "; outputs: " + string.Join(", ", Model.Outputs(value.node).Select(SlotName))));
                if (value.numbers != null)
                    return new CodeValue { numbers = member.Select(ch => Component(value.numbers, "xyzw".IndexOf(ch) >= 0 ? "xyzw".IndexOf(ch) : "rgba".IndexOf(ch))).ToArray() };
                if (value.node == null)
                    throw new ArgumentException(value.word + " is not a node or a variable.");
                // v.xyz of a Vector3 is v itself.
                if ("xyzw".StartsWith(member, StringComparison.Ordinal) || "rgba".StartsWith(member, StringComparison.Ordinal))
                {
                    Sg.Call(graph, "ValidateGraph");
                    if (VectorSize(value.slot ?? Model.Outputs(value.node).First()) == member.Length)
                        return value;
                }
                if (member.Length == 1)
                {
                    var split = Create("Split");
                    SetInput(split, Model.Inputs(split).First(), value);
                    var index = "xyzw".IndexOf(member[0]) >= 0 ? "xyzw".IndexOf(member[0]) : "rgba".IndexOf(member[0]);
                    return new CodeValue { node = split, slot = Model.Outputs(split)[index] };
                }
                var swizzle = Create("Swizzle");
                SetNodeField(window, Model, Model.Id(swizzle) + ".Mask", member, touched);
                SetInput(swizzle, Model.Inputs(swizzle).First(), value);
                return new CodeValue { node = swizzle };
            }

            private static float Component(float[] numbers, int index)
            {
                return numbers.Length == 1 ? numbers[0] : index < numbers.Length ? numbers[index] : 0f;
            }

            // float3(1, 0, 0) is a typed value; with nodes inside, a Combine of their components.
            private CodeValue Construct(CallExpr call)
            {
                var size = call.head.Length > 0 && char.IsDigit(call.head[call.head.Length - 1]) ? call.head[call.head.Length - 1] - '0' : 1;
                var values = call.args.Select(arg => arg.value == null ? throw new FormatException(arg.error) : Evaluate(arg.value)).ToList();
                if (values.Count > 1)
                    values = values.Select(Concrete).ToList();
                if (values.All(item => item.numbers != null))
                {
                    var numbers = values.SelectMany(item => item.numbers).ToArray();
                    return new CodeValue { numbers = numbers.Length == 1 ? Enumerable.Repeat(numbers[0], size).ToArray() : numbers };
                }
                if (values.Count == 1)
                    return values[0];
                Sg.Call(graph, "ValidateGraph");
                var components = new List<CodeValue>();
                foreach (var value in values)
                {
                    if (value.numbers != null)
                    {
                        components.AddRange(value.numbers.Select(item => new CodeValue { numbers = new[] { item } }));
                        continue;
                    }
                    var output = value.slot ?? Model.Outputs(value.node).First();
                    var width = Math.Max(1, VectorSize(output));
                    if (width == 1)
                    {
                        components.Add(value);
                        continue;
                    }
                    var split = Create("Split");
                    SetInput(split, Model.Inputs(split).First(), value);
                    components.AddRange(Model.Outputs(split).Take(width).Select(slot => new CodeValue { node = split, slot = slot }));
                }
                if (components.Count != size)
                    throw new ArgumentException(call.head + " takes " + size + " components, got " + components.Count);
                var combine = Create("Combine");
                var inputs = Model.Inputs(combine);
                for (var index = 0; index < components.Count; index++)
                    SetInput(combine, inputs[index], components[index]);
                var result = Model.Outputs(combine).FirstOrDefault(slot => SlotName(slot).Length == size);
                return new CodeValue { node = combine, slot = result };
            }
        }

        // asset-modify Code=<text>: the text applied to the graph, reported as what changed and the ids new names got.
        private static List<string> WriteCode(EditorWindow window, object graph, string text, List<object> touched)
        {
            var writer = new CodeWriter(window, graph, false);
            writer.Run(new CodeParser(text, false).Statements());
            touched.AddRange(writer.touched);
            return Report(writer, graph);
        }

        private static List<string> Report(CodeWriter writer, object graph)
        {
            var model = new Model(graph);
            var changes = new List<string>();
            if (writer.created.Count > 0)
                changes.Add("added: " + string.Join(", ", writer.created.Where(model.nodes.Contains).Select(model.Label)));
            if (writer.names.Count > 0)
                changes.Add("names: " + string.Join(", ", writer.names));
            if (writer.replaced.Count > 0)
                changes.Add("replaced: " + string.Join(", ", writer.replaced));
            if (writer.removed.Count > 0)
                changes.Add("removed: " + string.Join(", ", writer.removed));
            return changes;
        }

        // ---------- Convert To Nodes ----------

        // A Custom Function's HLSL as the nodes it computes, in place: its inputs feed them, its outputs' wires leave from them.
        private static string ConvertToNodes(EditorWindow window, object graph, object node, List<object> touched)
        {
            if (node.GetType().Name != "CustomFunctionNode")
                throw new ArgumentException("Convert To Nodes takes a Custom Function node.");
            var model = new Model(graph);
            var label = model.Label(node);
            var name = (string)Sg.Get(node, "functionName");
            var inputs = model.Inputs(node);
            var outputs = model.Outputs(node);
            List<string> parameters;
            string body;
            string file = null;
            var firstLine = 1;
            if (Sg.Get(node, "sourceType").ToString() == "File")
            {
                file = AssetDatabase.GUIDToAssetPath((string)Sg.Get(node, "functionSource"));
                if (string.IsNullOrEmpty(file) || !File.Exists(file))
                    throw new ArgumentException(label + " has no HLSL file.");
                var source = File.ReadAllText(file);
                var match = Regex.Match(source, @"\bvoid\s+" + Regex.Escape(name) + @"_float\s*\(([^)]*)\)\s*\{");
                if (!match.Success)
                    match = Regex.Match(source, @"\bvoid\s+" + Regex.Escape(name) + @"_half\s*\(([^)]*)\)\s*\{");
                if (!match.Success)
                    throw new ArgumentException(file + " has no void " + name + "_float(...) function.");
                parameters = match.Groups[1].Value.Split(',').Select(item => item.Trim().Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault())
                    .Where(item => !string.IsNullOrEmpty(item)).ToList();
                var open = match.Index + match.Length;
                var depth = 1;
                var index = open;
                for (; index < source.Length && depth > 0; index++)
                    depth += source[index] == '{' ? 1 : source[index] == '}' ? -1 : 0;
                body = source.Substring(open, index - 1 - open);
                firstLine = 1 + source.Substring(0, open).Count(ch => ch == '\n');
            }
            else
            {
                parameters = inputs.Concat(outputs).Select(slot => (string)Sg.Get(slot, "shaderOutputName")).ToList();
                body = (string)Sg.Get(node, "functionBody");
            }
            if (parameters.Count != inputs.Count + outputs.Count)
                throw new ArgumentException(name + " has " + parameters.Count + " parameters, the node " + (inputs.Count + outputs.Count) + " slots.");
            var writer = new CodeWriter(window, graph, true);
            var group = Sg.Get(node, "group");
            if (group == null)
            {
                group = Activator.CreateInstance(Sg.Type("UnityEditor.ShaderGraph.GroupData"), name, ((Rect)Sg.Get(Sg.Get(node, "drawState"), "position")).position);
                Undo(graph, "Create Group Node");
                Sg.Call(graph, "CreateGroup", group);
            }
            writer.SetGroup(group);
            var constants = new List<object>();
            for (var index = 0; index < inputs.Count; index++)
            {
                var value = InputValue(model, node, inputs[index]);
                if (value.numbers != null && VectorSize(inputs[index]) > 1)
                {
                    value = writer.Constant(value.numbers, VectorSize(inputs[index]));
                    constants.Add(value.node);
                }
                writer.Bind(parameters[index], value);
            }
            try
            {
                writer.Run(new CodeParser(body, true, firstLine).Statements());
            }
            catch (Exception error) when (error.Message.StartsWith("line ", StringComparison.Ordinal))
            {
                throw new ArgumentException((file ?? label + " Body") + " " + error.Message, error);
            }
            var targets = new List<KeyValuePair<object, CodeValue>>();
            for (var index = 0; index < outputs.Count; index++)
            {
                var value = writer.Variable(parameters[inputs.Count + index]);
                if (value == null)
                    throw new ArgumentException(name + " never assigns " + parameters[inputs.Count + index] + ".");
                targets.Add(new KeyValuePair<object, CodeValue>(outputs[index], value));
            }
            // Wires that left the Custom Function now leave from what its outputs computed.
            var fresh = new Model(graph);
            var moves = new List<Action>();
            foreach (var pair in targets)
            {
                var id = (int)Sg.Get(pair.Key, "id");
                foreach (var edge in Sg.Items(Sg.Get(graph, "edges")).ToList())
                {
                    var from = Sg.Get(edge, "outputSlot");
                    if (Sg.Get(from, "node") != node || (int)Sg.Get(from, "slotId") != id)
                        continue;
                    var to = Sg.Get(edge, "inputSlot");
                    var target = Sg.Get(to, "node");
                    var slot = fresh.Inputs(target).FirstOrDefault(item => (int)Sg.Get(item, "id") == (int)Sg.Get(to, "slotId"));
                    if (slot != null)
                        moves.Add(() => writer.SetInput(target, slot, pair.Value));
                    touched.Add(target);
                }
            }
            foreach (var move in moves)
                move();
            Undo(graph, "Delete Nodes");
            Sg.Call(graph, "RemoveElements", Sg.Array("UnityEditor.ShaderGraph.AbstractMaterialNode", new[] { node }), Sg.Array("UnityEditor.Graphing.IEdge", new object[0]),
                Sg.Array("UnityEditor.ShaderGraph.GroupData", new object[0]), Sg.Array("UnityEditor.ShaderGraph.StickyNoteData", new object[0]), null);
            writer.RemoveUnused(constants);
            touched.AddRange(writer.touched);
            var changes = Report(writer, graph);
            return label + " converted" + (changes.Count == 0 ? string.Empty : ": " + string.Join("; ", changes));
        }

        private static CodeValue InputValue(Model model, object node, object slot)
        {
            var edge = model.IncomingEdges(node, slot).FirstOrDefault();
            if (edge != null)
            {
                var output = Sg.Get(edge, "outputSlot");
                var source = Sg.Get(output, "node");
                return new CodeValue { node = source, slot = model.Outputs(source).FirstOrDefault(item => (int)Sg.Get(item, "id") == (int)Sg.Get(output, "slotId")) };
            }
            var value = SlotValue(slot) as string ?? "0";
            var parts = value.Split(',').Select(part => float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? (float?)number : null).ToList();
            return parts.All(part => part.HasValue) ? new CodeValue { numbers = parts.Select(part => part.Value).ToArray() } : new CodeValue { word = value };
        }
    }
}
