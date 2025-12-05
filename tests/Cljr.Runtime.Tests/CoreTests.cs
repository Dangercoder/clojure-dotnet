using Cljr;
using Cljr.Collections;
using static Cljr.Core;

namespace Cljr.Runtime.Tests;

public class CoreTests
{
    #region Equality Tests

    [Fact]
    public void Equals_NullValues_ReturnsTrue()
    {
        Assert.True(Core.Equals(null, null));
    }

    [Fact]
    public void Equals_NumbersWithDifferentTypes_ReturnsTrue()
    {
        Assert.True(Core.Equals(1, 1L));
        Assert.True(Core.Equals(1.0, 1));
        Assert.True(Core.Equals(1.0f, 1.0));
    }

    [Fact]
    public void Equals_Lists_ReturnsTrue()
    {
        var list1 = new List<object?> { 1, 2, 3 };
        var list2 = new List<object?> { 1, 2, 3 };
        Assert.True(Core.Equals(list1, list2));
    }

    [Fact]
    public void Equals_DifferentLists_ReturnsFalse()
    {
        var list1 = new List<object?> { 1, 2, 3 };
        var list2 = new List<object?> { 1, 2, 4 };
        Assert.False(Core.Equals(list1, list2));
    }

    [Fact]
    public void Equals_Dictionaries_ReturnsTrue()
    {
        var dict1 = new Dictionary<object, object?> { ["a"] = 1, ["b"] = 2 };
        var dict2 = new Dictionary<object, object?> { ["a"] = 1, ["b"] = 2 };
        Assert.True(Core.Equals(dict1, dict2));
    }

    #endregion

    #region Truthiness Tests

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(0, true)]
    [InlineData("", true)]
    [InlineData("hello", true)]
    public void IsTruthy_ReturnsExpected(object? value, bool expected)
    {
        Assert.Equal(expected, IsTruthy(value));
    }

    #endregion

    #region Collection Tests

    [Fact]
    public void Get_Dictionary_ReturnsValue()
    {
        var dict = new Dictionary<object, object?> { ["key"] = "value" };
        Assert.Equal("value", get(dict, "key"));
    }

    [Fact]
    public void Get_List_ReturnsValueAtIndex()
    {
        var list = new List<object?> { "a", "b", "c" };
        Assert.Equal("b", get(list, 1));
    }

    [Fact]
    public void Get_MissingKey_ReturnsNotFound()
    {
        var dict = new Dictionary<object, object?> { ["key"] = "value" };
        Assert.Equal("default", get(dict, "missing", "default"));
    }

    [Fact]
    public void Assoc_Dictionary_ReturnsNewDict()
    {
        var dict = new Dictionary<object, object?> { ["a"] = 1 };
        var result = assoc(dict, "b", 2) as Dictionary<object, object?>;
        Assert.NotNull(result);
        Assert.Equal(2, result["b"]);
        Assert.False(dict.ContainsKey("b")); // Original unchanged
    }

    [Fact]
    public void Conj_List_AppendsItem()
    {
        var list = new List<object?> { 1, 2 };
        var result = conj(list, 3) as List<object?>;
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal(3, result[2]);
    }

    [Fact]
    public void Dissoc_Dictionary_RemovesKey()
    {
        var dict = new Dictionary<object, object?> { ["a"] = 1, ["b"] = 2 };
        var result = dissoc(dict, "a") as Dictionary<object, object?>;
        Assert.NotNull(result);
        Assert.False(result.ContainsKey("a"));
        Assert.True(result.ContainsKey("b"));
    }

    #endregion

    #region Sequence Tests

    [Fact]
    public void First_List_ReturnsFirstElement()
    {
        var list = new List<object?> { 1, 2, 3 };
        Assert.Equal(1, first(list));
    }

    [Fact]
    public void First_EmptyList_ReturnsNull()
    {
        var list = new List<object?>();
        Assert.Null(first(list));
    }

    [Fact]
    public void Rest_List_ReturnsRemaining()
    {
        var list = new List<object?> { 1, 2, 3 };
        var result = rest(list).ToList();
        Assert.Equal(2, result.Count);
        Assert.Equal(2, result[0]);
        Assert.Equal(3, result[1]);
    }

    [Fact]
    public void Map_AppliesFunction()
    {
        var list = new List<object?> { 1L, 2L, 3L };
        var result = map(x => (long)x! * 2, list).ToList();
        Assert.Equal(new List<object?> { 2L, 4L, 6L }, result);
    }

    [Fact]
    public void Filter_FiltersElements()
    {
        var list = new List<object?> { 1L, 2L, 3L, 4L, 5L };
        var result = filter(x => (long)x! % 2 == 1, list).ToList();
        Assert.Equal(3, result.Count);
        Assert.Equal(1L, result[0]);
        Assert.Equal(3L, result[1]);
        Assert.Equal(5L, result[2]);
    }

    [Fact]
    public void Reduce_WithInit_ComputesResult()
    {
        var list = new List<object?> { 1L, 2L, 3L, 4L };
        var result = reduce((a, b) => (long)a! + (long)b!, 0L, list);
        Assert.Equal(10L, result);
    }

    [Fact]
    public void Take_ReturnsFirstN()
    {
        var list = new List<object?> { 1, 2, 3, 4, 5 };
        var result = take(3, list).ToList();
        Assert.Equal(3, result.Count);
        Assert.Equal(new List<object?> { 1, 2, 3 }, result);
    }

    [Fact]
    public void Drop_SkipsFirstN()
    {
        var list = new List<object?> { 1, 2, 3, 4, 5 };
        var result = drop(2, list).ToList();
        Assert.Equal(3, result.Count);
        Assert.Equal(new List<object?> { 3, 4, 5 }, result);
    }

    [Fact]
    public void Concat_CombinesSequences()
    {
        var list1 = new List<object?> { 1, 2 };
        var list2 = new List<object?> { 3, 4 };
        var result = concat(list1, list2).ToList();
        Assert.Equal(new List<object?> { 1, 2, 3, 4 }, result);
    }

    [Fact]
    public void Distinct_RemovesDuplicates()
    {
        var list = new List<object?> { 1, 2, 1, 3, 2, 4 };
        var result = distinct(list).ToList();
        Assert.Equal(new List<object?> { 1, 2, 3, 4 }, result);
    }

    [Fact]
    public void GroupBy_GroupsByKey()
    {
        var list = new List<object?> { 1L, 2L, 3L, 4L };
        var result = group_by(x => (long)x! % 2, list);
        Assert.Equal(2, result.Count);
        Assert.Equal(2, result[1L].Count); // odd: 1, 3
        Assert.Equal(2, result[0L].Count); // even: 2, 4
    }

    [Fact]
    public void Partition_ChunksSequence()
    {
        var list = new List<object?> { 1, 2, 3, 4, 5, 6 };
        var result = partition(2, list).ToList();
        Assert.Equal(3, result.Count);
        Assert.Equal(new List<object?> { 1, 2 }, result[0]);
        Assert.Equal(new List<object?> { 3, 4 }, result[1]);
        Assert.Equal(new List<object?> { 5, 6 }, result[2]);
    }

    [Fact]
    public void Range_GeneratesSequence()
    {
        var result = range(5).ToList();
        Assert.Equal(5, result.Count);
        Assert.Equal(new List<long> { 0, 1, 2, 3, 4 }, result);
    }

    [Fact]
    public void Range_WithStartEnd_GeneratesSequence()
    {
        var result = range(2, 7).ToList();
        Assert.Equal(new List<long> { 2, 3, 4, 5, 6 }, result);
    }

    [Fact]
    public void Cons_PrependElement()
    {
        var list = new List<object?> { 2, 3 };
        var result = cons(1, list).ToList();
        Assert.Equal(new List<object?> { 1, 2, 3 }, result);
    }

    #endregion

    #region Arithmetic Tests

    [Fact]
    public void Plus_AddsNumbers()
    {
        Assert.Equal(0, _PLUS_());
        Assert.Equal(5L, _PLUS_(5L));
        Assert.Equal(10L, _PLUS_(3L, 7L));
        Assert.Equal(15L, _PLUS_(1L, 2L, 3L, 4L, 5L));
    }

    [Fact]
    public void Minus_SubtractsNumbers()
    {
        Assert.Equal(-5L, _MINUS_(5L));
        Assert.Equal(4L, _MINUS_(10L, 6L));
        Assert.Equal(0L, _MINUS_(10L, 5L, 3L, 2L));
    }

    [Fact]
    public void Star_MultipliesNumbers()
    {
        Assert.Equal(1, _STAR_());
        Assert.Equal(5L, _STAR_(5L));
        Assert.Equal(12L, _STAR_(3L, 4L));
        Assert.Equal(120L, _STAR_(2L, 3L, 4L, 5L));
    }

    [Fact]
    public void Slash_DividesNumbers()
    {
        Assert.Equal(2L, _SLASH_(10L, 5L));
        Assert.Equal(2L, _SLASH_(24L, 3L, 4L));
    }

    [Fact]
    public void Inc_IncrementsNumber()
    {
        Assert.Equal(6L, inc(5L));
        Assert.Equal(1L, inc(0L));
    }

    [Fact]
    public void Dec_DecrementsNumber()
    {
        Assert.Equal(4L, dec(5L));
        Assert.Equal(-1L, dec(0L));
    }

    [Fact]
    public void Comparisons_Work()
    {
        Assert.True(_LT_(1L, 2L, 3L));
        Assert.False(_LT_(1L, 2L, 2L));
        Assert.True(_LT__EQ_(1L, 2L, 2L, 3L));
        Assert.True(_GT_(3L, 2L, 1L));
        Assert.True(_GT__EQ_(3L, 2L, 2L, 1L));
        Assert.True(_EQ_(1L, 1L, 1L));
        Assert.False(_EQ_(1L, 2L));
    }

    #endregion

    #region String Tests

    [Fact]
    public void Str_ConcatenatesValues()
    {
        Assert.Equal("", str());
        Assert.Equal("hello", str("hello"));
        Assert.Equal("hello world", str("hello", " ", "world"));
        Assert.Equal("123", str(1, 2, 3));
    }

    [Fact]
    public void Join_JoinsWithSeparator()
    {
        var list = new List<object?> { "a", "b", "c" };
        Assert.Equal("a,b,c", join(",", list));
        Assert.Equal("abc", join(list));
    }

    [Fact]
    public void StringFunctions_Work()
    {
        Assert.Equal("HELLO", upper_case("hello"));
        Assert.Equal("hello", lower_case("HELLO"));
        Assert.Equal("hello", trim("  hello  "));
        Assert.True(blank_QMARK_("  "));
        Assert.False(blank_QMARK_("hello"));
        Assert.True(starts_with_QMARK_("hello", "he"));
        Assert.True(ends_with_QMARK_("hello", "lo"));
        Assert.True(includes_QMARK_("hello", "ell"));
        Assert.Equal("hella", replace("hello", "o", "a"));
    }

    #endregion

    #region Predicate Tests

    [Fact]
    public void Predicates_Work()
    {
        Assert.True(nil_QMARK_(null));
        Assert.False(nil_QMARK_(1));
        Assert.True(some_QMARK_(1));
        Assert.False(some_QMARK_(null));
        Assert.True(number_QMARK_(42));
        Assert.True(string_QMARK_("hello"));
        Assert.True(vector_QMARK_(PersistentVector.Empty));
        Assert.True(map_QMARK_(new Dictionary<object, object?>()));
        Assert.True(fn_QMARK_((Func<int>)(() => 1)));
    }

    [Fact]
    public void NumericPredicates_Work()
    {
        Assert.True(zero_QMARK_(0));
        Assert.True(zero_QMARK_(0L));
        Assert.True(zero_QMARK_(0.0));
        Assert.True(pos_QMARK_(1L));
        Assert.False(pos_QMARK_(0L));
        Assert.True(neg_QMARK_(-1L));
        Assert.True(even_QMARK_(2L));
        Assert.True(odd_QMARK_(3L));
    }

    #endregion

    #region Apply Tests

    [Fact]
    public void Apply_AppliesFunctionToArgs()
    {
        // Use a lambda wrapping the function instead of method group
        Func<object?, object?, object?> plusFn = (a, b) => _PLUS_(a, b);
        var result = apply(plusFn, new List<object?> { 1L, 2L, 3L });
        Assert.Equal(6L, result);
    }

    [Fact]
    public void Apply_WithLeadingArgs_Works()
    {
        Func<object?, object?, object?> plusFn = (a, b) => _PLUS_(a, b);
        var result = apply(plusFn, 1L, new List<object?> { 2L, 3L });
        Assert.Equal(6L, result);
    }

    #endregion
}
