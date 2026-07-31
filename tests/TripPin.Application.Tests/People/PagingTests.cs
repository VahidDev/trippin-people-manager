using FluentAssertions;
using TripPin.Application.People.Models;
using Xunit;

namespace TripPin.Application.Tests.People;

public sealed class PagingTests
{
    [Fact]
    public void Valid_arguments_produce_no_errors()
    {
        Paging.Validate(1, Paging.MinPageSize).Should().BeEmpty();
        Paging.Validate(99, Paging.MaxPageSize).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_page_below_the_minimum_is_rejected(int page)
    {
        Paging.Validate(page, 8).Should().ContainSingle()
            .Which.Should().Be($"Page must be {Paging.MinPage} or greater.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(Paging.MaxPageSize + 1)]
    public void A_page_size_outside_the_bounds_is_rejected(int pageSize)
    {
        Paging.Validate(1, pageSize).Should().ContainSingle();
    }

    [Fact]
    public void Both_problems_are_reported_together()
    {
        Paging.Validate(0, 0).Should().HaveCount(2);
    }
}

public sealed class PagedResultTests
{
    [Fact]
    public void HasMore_is_true_while_pages_remain()
    {
        new PagedResult<string>(["a"], TotalCount: 20, Page: 1, PageSize: 8)
            .HasMore.Should().BeTrue();
    }

    [Fact]
    public void HasMore_is_false_on_the_last_page()
    {
        new PagedResult<string>(["a"], TotalCount: 20, Page: 3, PageSize: 8)
            .HasMore.Should().BeFalse();
    }

    /// <summary>
    /// The live service holds 20 people and pages at 8, so this is the real
    /// shape the list screen has to render.
    /// </summary>
    [Fact]
    public void The_boundary_case_matches_the_live_data_shape()
    {
        new PagedResult<string>([], 20, 2, 8).HasMore.Should().BeTrue();
        new PagedResult<string>([], 16, 2, 8).HasMore.Should().BeFalse();
    }

    [Fact]
    public void An_empty_result_has_no_further_pages()
    {
        new PagedResult<string>([], 0, 1, 8).HasMore.Should().BeFalse();
    }
}
