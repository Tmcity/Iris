using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Platform.Windows.Hooks;
using Shouldly;

namespace Iris.Platform.Windows.Tests.Hooks;

public sealed class GestureRecognizerTests
{
    private static MouseSample Sample(MouseMessage message, int x = 0, int y = 0, uint time = 100,
        bool shift = false, bool beam = true, uint flags = 0, int source = 1) =>
        new(message, new PixelPoint(x, y), time, flags, new MouseSource(source, (uint)source), beam, shift);
    private static GestureRecognizer New(int threshold = 4, bool beam = true) => new(new(threshold, beam, []));
    private static readonly ClickMetrics Metrics = new(500, 4, 4);
    private static SelectionGesture? Click(GestureRecognizer recognizer, int x = 0, int y = 0, uint time = 100,
        ClickMetrics? metrics = null, int source = 1, long generation = 1)
    {
        recognizer.Accept(Sample(MouseMessage.LeftDown, x, y, time, source: source), generation, metrics ?? Metrics).ShouldBeNull();
        return recognizer.Accept(Sample(MouseMessage.LeftUp, x, y, unchecked(time + 1), source: source), generation, metrics ?? Metrics);
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(3, 0, null)]
    [InlineData(4, 0, SelectionGesture.Drag)]
    [InlineData(-4, 0, SelectionGesture.Drag)]
    [InlineData(3, 3, SelectionGesture.Drag)]
    [InlineData(2, 3, null)]
    [InlineData(0, -4, SelectionGesture.Drag)]
    public void Drag_InclusiveEuclideanBoundary(int x, int y, SelectionGesture? expected)
    {
        var subject = New();
        subject.Accept(Sample(MouseMessage.LeftDown), 1, Metrics);
        subject.Accept(Sample(MouseMessage.LeftUp, x, y, 101), 1, Metrics).ShouldBe(expected);
    }

    [Theory]
    [InlineData(int.MinValue, int.MaxValue)]
    [InlineData(int.MaxValue, int.MinValue)]
    [InlineData(-100, -96)]
    public void Drag_ExtremeExcursionAndReturnAreRetained(int down, int excursion)
    {
        var subject = New();
        subject.Accept(Sample(MouseMessage.LeftDown, down, down), 1, Metrics);
        subject.Accept(Sample(MouseMessage.Move, excursion, excursion, 101), 1, Metrics).ShouldBeNull();
        subject.Accept(Sample(MouseMessage.LeftUp, down, down, 102), 1, Metrics).ShouldBe(SelectionGesture.Drag);
    }

    [Fact]
    public void Click_OneTwoThreeFourAndDownOnlyPriority()
    {
        var subject = New();
        Click(subject).ShouldBeNull();
        Click(subject, time: 200).ShouldBe(SelectionGesture.DoubleClick);
        Click(subject, time: 300).ShouldBe(SelectionGesture.TripleClick);
        Click(subject, time: 400).ShouldBeNull();
        subject.Accept(Sample(MouseMessage.LeftDown, time: 500, shift: true), 1, Metrics).ShouldBeNull();
        subject.Accept(Sample(MouseMessage.LeftUp, time: 501), 1, Metrics).ShouldBe(SelectionGesture.ShiftClick);
        Click(subject, time: 600).ShouldBeNull();
        subject.Accept(Sample(MouseMessage.LeftDown, time: 700, shift: true), 1, Metrics);
        subject.Accept(Sample(MouseMessage.LeftUp, 4, time: 701), 1, Metrics).ShouldBe(SelectionGesture.Drag);
    }

    [Theory]
    [InlineData(4, -2, true)]
    [InlineData(4, 1, true)]
    [InlineData(4, 2, false)]
    [InlineData(4, -3, false)]
    [InlineData(5, -2, true)]
    [InlineData(5, 2, true)]
    [InlineData(5, 3, false)]
    [InlineData(1, 0, true)]
    [InlineData(1, 1, false)]
    [InlineData(0, 0, false)]
    [InlineData(-1, 0, false)]
    public void Click_RectangleIsTotalHalfOpenDimension(int dimension, int displacement, bool matches)
    {
        foreach (bool horizontal in new[] { true, false })
        {
            var subject = New();
            var metrics = new ClickMetrics(500, dimension, dimension);
            Click(subject, metrics: metrics);
            Click(subject, horizontal ? displacement : 0, horizontal ? 0 : displacement, 200, metrics)
                .ShouldBe(matches ? SelectionGesture.DoubleClick : null);
        }
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Click_RectangleArithmeticWidensBeforeComputingEdges(int coordinate)
    {
        var subject = New();
        Click(subject, coordinate, coordinate);
        Click(subject, coordinate, coordinate, 200).ShouldBe(SelectionGesture.DoubleClick);
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public void Click_TimeInclusiveAndUintWrap(uint elapsed, bool matches)
    {
        var subject = New();
        uint start = uint.MaxValue - 200;
        Click(subject, time: start);
        Click(subject, time: unchecked(start + elapsed)).ShouldBe(matches ? SelectionGesture.DoubleClick : null);
    }

    [Theory]
    [InlineData((uint)MouseMessage.RightDown, 0)]
    [InlineData((uint)MouseMessage.MiddleDown, 0)]
    [InlineData((uint)MouseMessage.XDown, 0)]
    [InlineData((uint)MouseMessage.Wheel, 0)]
    [InlineData((uint)MouseMessage.HorizontalWheel, 0)]
    [InlineData((uint)MouseMessage.Move, 1)]
    [InlineData((uint)MouseMessage.LeftUp, 2)]
    public void InvalidInput_ResetsDragAndClickSequence(uint message, uint flags)
    {
        var subject = New();
        Click(subject);
        subject.Accept(Sample(MouseMessage.LeftDown, time: 200), 1, Metrics);
        subject.Accept(Sample((MouseMessage)message, time: 201, flags: flags), 1, Metrics).ShouldBeNull();
        subject.Accept(Sample(MouseMessage.LeftUp, 20, time: 202), 1, Metrics).ShouldBeNull();
        Click(subject, time: 300).ShouldBeNull();
    }

    [Fact]
    public void SourceGenerationLossAndBackwardTime_CannotBridgeSequences()
    {
        var subject = New();
        Click(subject);
        Click(subject, time: 200, source: 2).ShouldBeNull();
        Click(subject, time: 300, source: 2, generation: 2).ShouldBeNull();
        subject.Reset();
        Click(subject, time: 400, source: 2, generation: 2).ShouldBeNull();
        subject.Accept(Sample(MouseMessage.LeftDown, time: 500, source: 2), 2, Metrics);
        subject.Accept(Sample(MouseMessage.LeftUp, 50, time: 499, source: 2), 2, Metrics).ShouldBeNull();
        subject.Accept(Sample(MouseMessage.LeftDown, time: 600, source: 1), 2, Metrics);
        subject.Accept(Sample(MouseMessage.LeftUp, 50, time: 601, source: 2), 2, Metrics).ShouldBeNull();
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(true, true, SelectionGesture.Drag)]
    [InlineData(false, false, SelectionGesture.Drag)]
    public void Cursor_RequiredOnlyAtDown(bool require, bool beam, SelectionGesture? expected)
    {
        var subject = New(beam: require);
        subject.Accept(Sample(MouseMessage.LeftDown, beam: beam), 1, Metrics);
        subject.Accept(Sample(MouseMessage.LeftUp, 4, time: 101, beam: !beam), 1, Metrics).ShouldBe(expected);
    }
}
