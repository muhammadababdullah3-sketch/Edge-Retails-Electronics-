using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Desktop.Views;
using Xunit;
using Xunit.Abstractions;

namespace EdgeRetails.Desktop.PerformanceTests;

/// <summary>
/// LOGIN-SCROLL acceptance tests: the PIN keypad and sign-in button must stay reachable without
/// scrolling the login surface, at every supported window size and account count.
/// </summary>
/// <remarks>
/// <para>
/// The requirement under test is that <em>only</em> the account list is a scroll region. The login
/// shell — header, PIN boxes, helper text, keypad and sign-in — must remain on screen, so an
/// operator can select an account and authenticate without first dragging the whole login sideways
/// or vertically. Account cards are never hidden or suspended to achieve this; all supplied
/// accounts stay in the collection and remain reachable by scrolling the inner viewport.
/// </para>
/// <para>
/// These assertions are derived from real WPF measure/arrange passes on an STA thread, not from
/// arithmetic on the XAML constants. That distinction matters: the fixed chrome height includes
/// text metrics, DPI rounding and the drop-shadow margins, and an earlier hand-computed offset was
/// wrong by ~1.5px. Measuring is the only trustworthy oracle here.
/// </para>
/// </remarks>
public sealed class LoginScrollLayoutTests(ITestOutputHelper output)
{
    private static readonly object SyncLock = new();

    /// <summary>The smallest window size the login screen is required to support.</summary>
    private const double MinimumSupportedWindowHeight = 640.0;

    private static readonly double[] SupportedWindowHeights = { 640.0, 700.0, 768.0, 900.0, 1080.0 };
    private static readonly int[] AccountCounts = { 1, 12, 40 };

    private static void RunInSta(Action action)
    {
        lock (SyncLock)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    LoginScrollMeasureTests.EnsureApplicationInitializedForTests();
                    action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure is not null)
            {
                throw new Xunit.Sdk.XunitException(failure.ToString());
            }
        }
    }

    private sealed record Measured(
        double WindowHeight,
        int AccountCount,
        double OuterScrollable,
        double OuterViewport,
        double AccountViewport,
        double AccountExtent,
        double AccountScrollable,
        double KeypadBottom,
        double SignInBottom,
        int RenderedItems,
        double ActualHeight);

    /// <summary>Lays out the login view at the requested size and records the real geometry.</summary>
    private Measured Measure(double windowHeight, int accountCount)
    {
        const double width = 1024.0;
        var view = new LoginView
        {
            DataContext = new FixedAccountsViewModel(accountCount),
            Width = width,
            Height = windowHeight,
        };

        view.Measure(new Size(width, windowHeight));
        view.Arrange(new Rect(0, 0, width, windowHeight));
        view.UpdateLayout();

        var scrollers = FindAll<ScrollViewer>(view).ToList();

        // The account viewport is the only 342px-wide scroll viewer; the outer shell is the other.
        var outer = scrollers.FirstOrDefault(s => s.Width is not 342);
        var accounts = scrollers.FirstOrDefault(s => s.Width is 342);
        var items = FindAll<ItemsControl>(view).FirstOrDefault();
        var keypad = FindAll<Grid>(view).FirstOrDefault(g => g.Width is 342 && g.RowDefinitions.Count == 7);
        var signIn = FindAll<Button>(view).FirstOrDefault(b => b.Width is 342 && b.Height is 42);

        Assert.NotNull(outer);
        Assert.NotNull(accounts);
        Assert.NotNull(items);
        Assert.NotNull(keypad);
        Assert.NotNull(signIn);

        var measured = new Measured(
            windowHeight,
            accountCount,
            outer!.ScrollableHeight,
            outer.ViewportHeight,
            accounts!.ActualHeight,
            accounts.ExtentHeight,
            accounts.ScrollableHeight,
            Bottom(keypad!, view),
            Bottom(signIn!, view),
            items!.Items.Count,
            view.ActualHeight);

        output.WriteLine(
            $"win={measured.WindowHeight} n={measured.AccountCount} " +
            $"OUTERscrollable={measured.OuterScrollable:F1} OUTERviewport={measured.OuterViewport:F1} " +
            $"ACCviewport={measured.AccountViewport:F1} ACCextent={measured.AccountExtent:F1} " +
            $"ACCscrollable={measured.AccountScrollable:F1} " +
            $"keypadBottom={measured.KeypadBottom:F1} signInBottom={measured.SignInBottom:F1} " +
            $"items={measured.RenderedItems} viewH={measured.ActualHeight:F1}");

        return measured;
    }

    public static TheoryData<double, int> SupportedSizes()
    {
        var data = new TheoryData<double, int>();
        foreach (var height in SupportedWindowHeights)
        {
            foreach (var count in AccountCounts)
            {
                data.Add(height, count);
            }
        }

        return data;
    }

    /// <summary>
    /// The core LOGIN-SCROLL invariant: the outer login surface never scrolls at a supported size.
    /// </summary>
    /// <remarks>
    /// A non-zero <see cref="ScrollViewer.ScrollableHeight"/> means part of the login shell has left
    /// the visible area and the operator must scroll the whole page to reach the sign-in control.
    /// </remarks>
    [Theory]
    [MemberData(nameof(SupportedSizes))]
    public void OuterLoginSurface_DoesNotScroll_AtSupportedSizes(double windowHeight, int accountCount)
    {
        RunInSta(() =>
        {
            var m = Measure(windowHeight, accountCount);

            Assert.True(
                m.OuterScrollable <= 0.5,
                $"Outer login surface scrolled by {m.OuterScrollable:F1}px at window height {windowHeight} " +
                $"with {accountCount} account(s); the login shell must never scroll at a supported size.");
        });
    }

    /// <summary>
    /// The sign-in button and keypad must sit inside the window, not below the fold.
    /// </summary>
    [Theory]
    [MemberData(nameof(SupportedSizes))]
    public void KeypadAndSignIn_RemainWithinWindow(double windowHeight, int accountCount)
    {
        RunInSta(() =>
        {
            var m = Measure(windowHeight, accountCount);

            Assert.True(
                m.KeypadBottom <= m.ActualHeight,
                $"PIN keypad bottom ({m.KeypadBottom:F1}) falls below the window ({m.ActualHeight:F1}) " +
                $"at height {windowHeight} with {accountCount} account(s).");

            Assert.True(
                m.SignInBottom <= m.ActualHeight,
                $"Sign-in button bottom ({m.SignInBottom:F1}) falls below the window ({m.ActualHeight:F1}) " +
                $"at height {windowHeight} with {accountCount} account(s).");
        });
    }

    /// <summary>
    /// The account viewport absorbs the remaining height and is bounded, so a tall window cannot
    /// push the keypad off-screen and a short window cannot collapse the list to nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(SupportedSizes))]
    public void AccountViewport_IsBoundedAndUsable(double windowHeight, int accountCount)
    {
        RunInSta(() =>
        {
            var m = Measure(windowHeight, accountCount);

            // Cap: 220px. The list must never grow past this, or the keypad is pushed out of view.
            Assert.True(
                m.AccountViewport <= 220.5,
                $"Account viewport grew to {m.AccountViewport:F1}px at height {windowHeight}, " +
                "exceeding the 220px cap that keeps the keypad visible.");

            // Floor: 44px, enforced by MinHeight in the view. A collapsed list is unusable.
            Assert.True(
                m.AccountViewport >= 44.0,
                $"Account viewport collapsed to {m.AccountViewport:F1}px at height {windowHeight}, " +
                "below the 44px minimum usable height.");
        });
    }

    /// <summary>
    /// Scrolling is a genuine solution, not a fig leaf: every account stays loaded and the inner
    /// list really does scroll when the content exceeds the viewport.
    /// </summary>
    [Theory]
    [MemberData(nameof(SupportedSizes))]
    public void EveryAccountRemainsLoadedAndScrollable(double windowHeight, int accountCount)
    {
        RunInSta(() =>
        {
            var m = Measure(windowHeight, accountCount);

            // Hiding or suspending accounts is explicitly not an acceptable way to satisfy the bound.
            Assert.Equal(accountCount, m.RenderedItems);

            if (m.AccountExtent > m.AccountViewport)
            {
                Assert.True(
                    m.AccountScrollable > 0.0,
                    $"Account content ({m.AccountExtent:F1}px) overflows the viewport " +
                    $"({m.AccountViewport:F1}px) but reports no scrollable height, so the overflow " +
                    "accounts would be unreachable.");
            }
            else
            {
                Assert.True(
                    m.AccountScrollable <= 0.5,
                    $"Account content fits the viewport but reports {m.AccountScrollable:F1}px of " +
                    "scrollable height.");
            }
        });
    }

    /// <summary>
    /// Non-vacuity guard. Proves the harness can actually observe outer scrolling, so a green
    /// <see cref="OuterLoginSurface_DoesNotScroll_AtSupportedSizes"/> is meaningful.
    /// </summary>
    /// <remarks>
    /// Below the supported minimum the outer scroll viewer is documented as a last-resort safety
    /// net. If the harness cannot detect scrolling even there, then it is not measuring anything
    /// and the invariant tests above would pass no matter what the layout did.
    /// </remarks>
    [Fact]
    public void Harness_DetectsOuterScrollBelowSupportedMinimum()
    {
        RunInSta(() =>
        {
            var m = Measure(600.0, 40);

            Assert.True(
                m.OuterScrollable > 0.0,
                "Harness failed to detect outer scrolling at 600px (below the supported minimum). " +
                "The layout invariant tests are therefore vacuous: they could not distinguish a " +
                "correct layout from a broken one.");
        });
    }

    private static double Bottom(FrameworkElement element, Visual root)
    {
        var bounds = element.TransformToAncestor(root)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Bottom;
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindAll<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class FixedAccountsViewModel(int count) : ViewModelBase
    {
        public ObservableCollection<LoginAccountItemViewModel> Accounts { get; } =
            new(Enumerable.Range(1, count).Select(i => new LoginAccountItemViewModel(
                i.ToString(), $"Synthetic User {i}", "Cashier", $"U{i}", $"U{i}", true, i == 1)));
    }
}
