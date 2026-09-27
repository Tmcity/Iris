using System;
using System.Text;

namespace Iris.Providers.Translation.OpenAICompatible;

/// <summary>
/// 剥离译文开头的 &lt;think&gt;…&lt;/think&gt; 标签及其后的前导空白。
/// 仅处理开头部分；译文中间出现的标签原样保留。
/// 每次请求一个实例，非线程安全。
/// </summary>
internal sealed class ThinkTagStripper
{
    private const string OpenTag = "<think>";
    private const string CloseTag = "</think>";

    private enum State
    {
        Detecting,
        InThink,
        TrimmingAfter,
        PassThrough,
    }

    private State _state = State.Detecting;
    private readonly StringBuilder _detectBuffer = new();
    private readonly StringBuilder _closeTagTail = new();

    public string Process(string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return string.Empty;
        }

        if (_state == State.PassThrough)
        {
            return delta;
        }

        var sb = new StringBuilder();
        var index = 0;

        while (index < delta.Length)
        {
            switch (_state)
            {
                case State.Detecting:
                {
                    var c = delta[index++];
                    var wsCount = CountLeadingWhitespace(_detectBuffer);
                    if (_detectBuffer.Length == wsCount && char.IsWhiteSpace(c))
                    {
                        _detectBuffer.Append(c);
                        break;
                    }

                    var tagCharIndex = _detectBuffer.Length - wsCount;
                    if (tagCharIndex < OpenTag.Length && c == OpenTag[tagCharIndex])
                    {
                        _detectBuffer.Append(c);
                        if (_detectBuffer.Length - wsCount == OpenTag.Length)
                        {
                            _detectBuffer.Clear();
                            _state = State.InThink;
                        }
                    }
                    else
                    {
                        _state = State.PassThrough;
                        sb.Append(_detectBuffer);
                        _detectBuffer.Clear();
                        sb.Append(c);
                        if (index < delta.Length)
                        {
                            sb.Append(delta.AsSpan(index));
                            index = delta.Length;
                        }
                    }
                    break;
                }

                case State.InThink:
                {
                    var remaining = delta.AsSpan(index);
                    var combined = _closeTagTail.ToString() + remaining.ToString();
                    var matchIdx = combined.IndexOf(CloseTag, StringComparison.Ordinal);

                    if (matchIdx >= 0)
                    {
                        var matchedEndInCombined = matchIdx + CloseTag.Length;
                        var charsFromTail = _closeTagTail.Length;
                        var deltaConsumed = matchedEndInCombined - charsFromTail;

                        index += deltaConsumed;
                        _closeTagTail.Clear();
                        _state = State.TrimmingAfter;
                    }
                    else
                    {
                        _closeTagTail.Clear();
                        var tailLen = Math.Min(combined.Length, CloseTag.Length - 1);
                        if (tailLen > 0)
                        {
                            _closeTagTail.Append(combined.AsSpan(combined.Length - tailLen));
                        }
                        index = delta.Length;
                    }
                    break;
                }

                case State.TrimmingAfter:
                {
                    var c = delta[index++];
                    if (char.IsWhiteSpace(c))
                    {
                        break;
                    }

                    _state = State.PassThrough;
                    sb.Append(c);
                    if (index < delta.Length)
                    {
                        sb.Append(delta.AsSpan(index));
                        index = delta.Length;
                    }
                    break;
                }

                case State.PassThrough:
                {
                    sb.Append(delta.AsSpan(index));
                    index = delta.Length;
                    break;
                }
            }
        }

        return sb.ToString();
    }

    public string Flush()
    {
        switch (_state)
        {
            case State.Detecting:
            {
                var result = _detectBuffer.ToString();
                _detectBuffer.Clear();
                _state = State.PassThrough;
                return result;
            }
            case State.InThink:
            case State.TrimmingAfter:
            {
                _closeTagTail.Clear();
                _state = State.PassThrough;
                return string.Empty;
            }
            default:
                return string.Empty;
        }
    }

    private static int CountLeadingWhitespace(StringBuilder sb)
    {
        var count = 0;
        for (var i = 0; i < sb.Length; i++)
        {
            if (char.IsWhiteSpace(sb[i]))
            {
                count++;
            }
            else
            {
                break;
            }
        }
        return count;
    }
}
