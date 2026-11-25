using Hokusai.Infer;

namespace Hokusai.Tests.Infer;

public sealed class AlignmentTests
{
    private static List<AlignedNode> Align(params string[] demos) =>
        Aligner.Align([.. demos.Select(d => (IReadOnlyList<string>)[.. d.Split(' ', StringSplitOptions.RemoveEmptyEntries)])]);

    [Fact]
    public void Identical_sequences_align_one_to_one()
    {
        var nodes = Align("A B C", "A B C");

        Assert.Equal(3, nodes.Count);
        Assert.All(nodes, n => Assert.Equal(2, n.Presence.Count));
    }

    [Fact]
    public void Statement_present_in_some_demonstrations_becomes_conditional()
    {
        var nodes = Align("A T C", "A C", "A T C");

        var t = Assert.IsType<AlignedStatement>(nodes[1]);
        Assert.Equal("T", t.Shape);
        Assert.Equal([0, 2], t.Presence.Order());
    }

    [Fact]
    public void Repeated_block_becomes_loop_with_iterations_per_demonstration()
    {
        var nodes = Align("H L S L S L S F", "H L S F");

        Assert.Equal(3, nodes.Count);
        var loop = Assert.IsType<AlignedLoop>(nodes[1]);
        Assert.Equal(["L", "S"], loop.Body);
        Assert.Equal(3, loop.Iterations[0].Count);
        Assert.Single(loop.Iterations[1]);
        Assert.Equal([1, 2], loop.Iterations[0][0]);
        Assert.Equal([5, 6], loop.Iterations[0][2]);
    }

    [Fact]
    public void Loop_absent_from_a_demonstration_has_partial_presence()
    {
        var nodes = Align("H L L F", "H F");

        var loop = Assert.IsType<AlignedLoop>(nodes[1]);
        Assert.Equal([0], loop.Presence);
    }

    [Fact]
    public void Single_statement_repetition_is_a_loop_and_self_repeating_bodies_are_not_duplicated()
    {
        var bodies = Aligner.FindLoopBodies([["A", "A", "A", "A"]]);

        Assert.Equal(["A"], Assert.Single(bodies));
    }

    [Fact]
    public void Conditional_inside_otherwise_identical_sequences_keeps_order()
    {
        var nodes = Align("A B X D", "A B D", "A Y B D");

        Assert.Equal(["A", "Y", "B", "X", "D"], nodes.Cast<AlignedStatement>().Select(n => n.Shape));
    }
}
