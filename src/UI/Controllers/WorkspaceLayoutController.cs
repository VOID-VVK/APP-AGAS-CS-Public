using System;
using System.Linq;
using AppAgasCs.Core.Resources;
using Godot;

namespace AGAS.UI.Controllers;

/// <summary>
/// Centralizes workspace panel geometry and persisted layout normalization. The controller
/// mutates only the Godot layout Resource supplied by MainWorkspace, preserving serialization.
/// </summary>
internal sealed class WorkspaceLayoutController
{
    internal const int CurrentResourceVersion = 2;
    internal const int DefaultLeftPanelWidth = 280;
    internal const int DefaultRightPanelWidth = 280;
    internal const int DefaultBottomDockHeight = 240;
    internal const int MinLeftPanelWidth = 220;
    internal const int MinRightPanelWidth = 240;
    // No fixed upper bound on dock size — players may expand left/right/bottom to nearly
    // fill the window. ClampPaneSize still reserves the sibling pane's minimum so the
    // center (or top workspace) is not permanently zeroed and can be dragged back.
    internal const int MaxLeftPanelWidth = int.MaxValue;
    internal const int MaxRightPanelWidth = int.MaxValue;
    internal const int MinCenterWidth = 320;
    internal const int CompactWorkspaceWidth = 1100;
    internal const int CompactLeftPanelWidth = 190;
    internal const int CompactRightPanelWidth = 220;
    internal const int CompactCenterWidth = 360;
    internal const int MinBottomDockHeight = 160;
    internal const int MaxBottomDockHeight = int.MaxValue;
    internal const int MinWorkspaceTopHeight = 260;
    internal const int MinCanvasHeight = 260;
    internal const int CollapsedDockThickness = 32;

    internal WorkspaceLayoutResource NormalizeLoadedLayout(
        WorkspaceLayoutResource layout,
        bool requiresReadOnly)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!requiresReadOnly && layout.ResourceVersion < CurrentResourceVersion)
        {
            layout.LeftPanelWidth = layout.LeftPanelWidth <= 0 ? DefaultLeftPanelWidth : layout.LeftPanelWidth;
            layout.RightPanelWidth = layout.RightPanelWidth <= 0 ? DefaultRightPanelWidth : layout.RightPanelWidth;
            layout.BottomDockHeight = layout.BottomDockHeight <= 0 ? DefaultBottomDockHeight : layout.BottomDockHeight;
            layout.BottomDockCollapsed = true;
            layout.ResourceVersion = CurrentResourceVersion;
        }
        return layout;
    }

    internal bool IsCompact(float workspaceWidth) =>
        workspaceWidth > 0 && workspaceWidth <= CompactWorkspaceWidth;

    internal void CapturePanelSizes(
        WorkspaceLayoutResource layout,
        float workspaceWidth,
        bool leftCollapsed,
        bool rightCollapsed,
        bool bottomCollapsed,
        bool rightDockVisible,
        SplitContainer? mainHorizontalSplit,
        SplitContainer? centerRightSplit,
        SplitContainer? workspaceBottomSplit,
        Control? canvas,
        bool captureHorizontalSizes = false)
    {
        bool compact = IsCompact(workspaceWidth);
        int minLeft = compact ? CompactLeftPanelWidth : MinLeftPanelWidth;
        int minRight = compact ? CompactRightPanelWidth : MinRightPanelWidth;
        int minCenter = compact ? CompactCenterWidth : MinCenterWidth;
        int centerRightSeparation = centerRightSplit?.GetThemeConstant("separation") ?? 0;

        if (captureHorizontalSizes && !leftCollapsed)
        {
            int available = mainHorizontalSplit == null ? 0 : GetAvailableSplitSize(mainHorizontalSplit);
            int actual = GetLeadingPaneSize(mainHorizontalSplit, (int)Math.Round(layout.LeftPanelWidth));
            layout.LeftPanelWidth = available > 0
                ? ClampPaneSize(actual, available, minLeft, MaxLeftPanelWidth,
                    minCenter + centerRightSeparation + (rightCollapsed ? CollapsedDockThickness : minRight))
                : actual;
        }

        if (captureHorizontalSizes && !rightCollapsed && rightDockVisible)
        {
            int available = centerRightSplit == null ? 0 : GetAvailableSplitSize(centerRightSplit);
            int actual = GetTrailingPaneSize(centerRightSplit, (int)Math.Round(layout.RightPanelWidth));
            layout.RightPanelWidth = available > 0
                ? ClampPaneSize(actual, available, minRight, MaxRightPanelWidth, minCenter)
                : actual;
        }

        if (!bottomCollapsed)
        {
            int available = workspaceBottomSplit == null ? 0 : GetAvailableSplitSize(workspaceBottomSplit);
            int actual = GetTrailingPaneSize(
                workspaceBottomSplit,
                (int)Math.Round(layout.BottomDockHeight));
            int desired = (int)Math.Round(layout.BottomDockHeight);
            int expected = available > 0
                ? ClampPaneSize(desired, available, MinBottomDockHeight, MaxBottomDockHeight,
                    GetMinimumTopHeight(workspaceBottomSplit!, canvas))
                : Math.Clamp(desired, MinBottomDockHeight, MaxBottomDockHeight);
            // A viewport clamp is temporary; keep the preferred height so it can return
            // when the window grows. A real splitter drag still replaces the preference.
            if (Math.Abs(actual - expected) > 1)
            {
                layout.BottomDockHeight = actual;
            }
        }
    }

    internal void ApplyOuterSplitLayout(
        WorkspaceLayoutResource layout,
        float workspaceWidth,
        bool leftCollapsed,
        bool rightCollapsed,
        bool bottomCollapsed,
        SplitContainer? mainHorizontalSplit,
        SplitContainer? centerRightSplit,
        SplitContainer? workspaceBottomSplit,
        Control? canvas,
        bool respectManualWidths = false)
    {
        bool compact = IsCompact(workspaceWidth);
        int minLeft = compact ? CompactLeftPanelWidth : MinLeftPanelWidth;
        int minRight = compact ? CompactRightPanelWidth : MinRightPanelWidth;
        int minCenter = compact ? CompactCenterWidth : MinCenterWidth;
        int centerRightSeparation = centerRightSplit?.GetThemeConstant("separation") ?? 0;

        if (mainHorizontalSplit != null)
        {
            int desiredLeft = leftCollapsed ? CollapsedDockThickness : (int)Math.Round(layout.LeftPanelWidth);
            if (!leftCollapsed && !respectManualWidths)
            {
                int startupWidth = (int)Math.Round(workspaceWidth * 0.20);
                desiredLeft = desiredLeft == DefaultLeftPanelWidth ? startupWidth : Math.Min(desiredLeft, startupWidth);
            }
            int minimum = leftCollapsed ? CollapsedDockThickness : minLeft;
            int maximum = leftCollapsed ? CollapsedDockThickness : MaxLeftPanelWidth;
            int otherMinimum = minCenter + centerRightSeparation
                + (rightCollapsed ? CollapsedDockThickness : minRight);
            Control? left = mainHorizontalSplit.GetChildren().OfType<Control>().FirstOrDefault(child => child.Visible);
            if (left != null)
            {
                int width = ClampPaneSize(desiredLeft, GetAvailableSplitSize(mainHorizontalSplit), minimum, maximum, otherMinimum);
                left.CustomMinimumSize = new Vector2(width, 1);
                left.SizeFlagsHorizontal = Control.SizeFlags.Fill;
                mainHorizontalSplit.SplitOffsets = new[] { 0 };
            }
        }

        if (workspaceBottomSplit != null)
        {
            Control? bottom = workspaceBottomSplit.GetChildren().OfType<Control>().Where(child => child.Visible).LastOrDefault();
            int bottomMinimum = compact ? 120 : MinBottomDockHeight;
            if (bottom != null && !bottomCollapsed)
            {
                bottom.CustomMinimumSize = new Vector2(1, bottomMinimum);
                bottomMinimum = Math.Max(bottomMinimum, (int)Math.Ceiling(bottom.GetMinimumSize().Y));
            }
            int minTopHeight = GetMinimumTopHeight(workspaceBottomSplit, canvas);
            int bottomHeight = bottomCollapsed
                ? CollapsedDockThickness
                : Math.Clamp((int)Math.Round(layout.BottomDockHeight), bottomMinimum, MaxBottomDockHeight);
            SetTrailingPaneSize(
                workspaceBottomSplit,
                bottomHeight,
                bottomCollapsed ? CollapsedDockThickness : bottomMinimum,
                bottomCollapsed ? CollapsedDockThickness : MaxBottomDockHeight,
                minTopHeight);
        }
    }

    private static int GetMinimumTopHeight(SplitContainer split, Control? canvas)
    {
        Control? top = split.GetChildren().OfType<Control>().FirstOrDefault(child => child.Visible);
        int minimum = Math.Max(MinWorkspaceTopHeight,
            (int)Math.Ceiling(top?.GetCombinedMinimumSize().Y ?? 0));
        if (top == null || canvas == null || !canvas.Visible || top.Size.Y <= 0 || canvas.Size.Y <= 0)
        {
            return minimum;
        }

        int fixedHeight = Math.Max(0, (int)Math.Round(top.Size.Y - canvas.Size.Y));
        int available = GetAvailableSplitSize(split);
        Control? bottom = split.GetChildren().OfType<Control>().Where(child => child.Visible).LastOrDefault();
        int bottomMinimum = (int)Math.Ceiling(bottom?.GetCombinedMinimumSize().Y ?? MinBottomDockHeight);
        int canvasHeight = Math.Min(MinCanvasHeight, Math.Max(100, available - fixedHeight - bottomMinimum));
        return Math.Max(minimum, fixedHeight + canvasHeight);
    }

    internal void ApplyCenterRightLayout(
        WorkspaceLayoutResource layout,
        float workspaceWidth,
        bool rightCollapsed,
        HSplitContainer centerRightSplit,
        PanelContainer rightDock,
        bool respectManualWidths = false)
    {
        if (!rightDock.Visible)
        {
            return;
        }

        bool compact = IsCompact(workspaceWidth);
        int minRight = compact ? CompactRightPanelWidth : MinRightPanelWidth;
        int minCenter = compact ? CompactCenterWidth : MinCenterWidth;
        int available = GetAvailableSplitSize(centerRightSplit);

        if (rightCollapsed)
        {
            rightDock.CustomMinimumSize = new Vector2(CollapsedDockThickness, 1);
            rightDock.SizeFlagsHorizontal = Control.SizeFlags.Fill;
            centerRightSplit.SplitOffsets = new[] { 0 };
            return;
        }

        int desiredRight = (int)Math.Round(layout.RightPanelWidth);
        if (!respectManualWidths)
        {
            int startupWidth = (int)Math.Round(workspaceWidth * 0.17);
            desiredRight = desiredRight == DefaultRightPanelWidth ? startupWidth : Math.Min(desiredRight, startupWidth);
        }
        int clampedRight = available > 0
            ? ClampPaneSize(desiredRight, available, minRight, MaxRightPanelWidth, minCenter)
            : Math.Clamp(desiredRight, minRight, MaxRightPanelWidth);
        rightDock.CustomMinimumSize = new Vector2(clampedRight, 1);
        rightDock.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        if (centerRightSplit.GetChildCount() > 0 && centerRightSplit.GetChild(0) is Control center)
        {
            center.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        }
        centerRightSplit.SplitOffsets = new[] { 0 };
    }

    internal void ApplyResponsiveMinimums(
        float workspaceWidth,
        Control? leftPanel,
        Control? rightPanel,
        PanelContainer? leftDock,
        PanelContainer? rightDock,
        bool leftCollapsed,
        bool rightCollapsed,
        WorkspaceLayoutResource? layout)
    {
        bool compact = IsCompact(workspaceWidth);
        int minLeft = compact ? CompactLeftPanelWidth : MinLeftPanelWidth;
        int minRight = compact ? CompactRightPanelWidth : MinRightPanelWidth;
        if (leftPanel != null) leftPanel.CustomMinimumSize = new Vector2(minLeft, 1);
        if (leftPanel?.FindChild("FilesScenesContentScroll", true, false) is ScrollContainer filesScroll)
        {
            filesScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Auto;
            filesScroll.GetHScrollBar().CustomMinimumSize = new Vector2(1, 16);
        }
        if (rightPanel != null) rightPanel.CustomMinimumSize = new Vector2(minRight, 1);
        if (leftDock != null && !leftCollapsed)
        {
            leftDock.CustomMinimumSize = new Vector2(minLeft, 1);
        }
        if (rightDock != null && !rightCollapsed && layout != null && rightDock.Visible)
        {
            rightDock.CustomMinimumSize = new Vector2(minRight, 1);
            rightDock.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        }
    }

    internal void PrepareRightDockForDrag(float workspaceWidth, PanelContainer rightDock)
    {
        int minimum = IsCompact(workspaceWidth) ? CompactRightPanelWidth : MinRightPanelWidth;
        rightDock.CustomMinimumSize = new Vector2(minimum, 1);
    }

    internal int ClampExpandedRightWidth(double desiredWidth, float workspaceWidth) =>
        Math.Clamp(
            (int)Math.Round(desiredWidth),
            IsCompact(workspaceWidth) ? CompactRightPanelWidth : MinRightPanelWidth,
            MaxRightPanelWidth);

    internal int GetTrailingPaneSize(SplitContainer? split, int fallback)
    {
        if (split == null)
        {
            return fallback;
        }
        int available = GetAvailableSplitSize(split);
        return available > 0 ? Math.Max(0, available - GetLeadingPaneSize(split, fallback)) : fallback;
    }

    internal static int ClampPaneSize(
        int desiredSize,
        int available,
        int minTargetSize,
        int maxTargetSize,
        int minOtherSize)
    {
        if (available <= 0)
        {
            return 0;
        }
        int upper = Math.Min(maxTargetSize, Math.Max(0, available - Math.Min(minOtherSize, available)));
        int lower = Math.Min(minTargetSize, upper);
        return Math.Clamp(desiredSize, lower, upper);
    }

    private static void SetLeadingPaneSize(
        SplitContainer split,
        int desiredFirstSize,
        int minFirstSize,
        int maxFirstSize,
        int minSecondSize)
    {
        int available = GetAvailableSplitSize(split);
        if (available <= 0)
        {
            return;
        }
        int firstSize = ClampPaneSize(desiredFirstSize, available, minFirstSize, maxFirstSize, minSecondSize);
        if (minFirstSize == maxFirstSize)
        {
            split.SplitOffsets = new[] { -available };
            return;
        }
        int currentFirstSize = GetActualLeadingPaneSize(split, available);
        int currentOffset = split.SplitOffsets.Length > 0 ? split.SplitOffsets[0] : 0;
        split.SplitOffsets = new[] { currentOffset + firstSize - currentFirstSize };
    }

    private static void SetTrailingPaneSize(
        SplitContainer split,
        int desiredSecondSize,
        int minSecondSize,
        int maxSecondSize,
        int minFirstSize)
    {
        int available = GetAvailableSplitSize(split);
        if (available <= 0)
        {
            return;
        }
        int secondSize = ClampPaneSize(desiredSecondSize, available, minSecondSize, maxSecondSize, minFirstSize);
        if (minSecondSize == maxSecondSize)
        {
            split.SplitOffsets = new[] { available };
            return;
        }
        int defaultFirstSize = GetDefaultLeadingPaneSize(split, available);
        split.SplitOffsets = new[] { available - secondSize - defaultFirstSize };
    }

    private static int GetLeadingPaneSize(SplitContainer? split, int fallback)
    {
        if (split == null)
        {
            return fallback;
        }
        int available = GetAvailableSplitSize(split);
        return available > 0 ? GetActualLeadingPaneSize(split, available) : fallback;
    }

    private static int GetAvailableSplitSize(SplitContainer split)
    {
        int total = split is HSplitContainer ? (int)Math.Round(split.Size.X) : (int)Math.Round(split.Size.Y);
        return Math.Max(0, total - split.GetThemeConstant("separation"));
    }

    private static int GetActualLeadingPaneSize(SplitContainer split, int available)
    {
        Control? first = split.GetChildren().OfType<Control>().FirstOrDefault(child => child.Visible);
        if (first == null) return available / 2;
        float size = split is HSplitContainer ? first.Size.X : first.Size.Y;
        return size > 0
            ? Math.Clamp((int)Math.Round(size), 0, available)
            : GetDefaultLeadingPaneSize(split, available);
    }

    private static int GetDefaultLeadingPaneSize(SplitContainer split, int available)
    {
        Control[] children = split.GetChildren().OfType<Control>().Where(child => child.Visible).Take(2).ToArray();
        if (children.Length < 2) return available / 2;
        bool horizontal = split is HSplitContainer;
        int firstMinimum = (int)Math.Ceiling(horizontal
            ? children[0].GetCombinedMinimumSize().X
            : children[0].GetCombinedMinimumSize().Y);
        int secondMinimum = (int)Math.Ceiling(horizontal
            ? children[1].GetCombinedMinimumSize().X
            : children[1].GetCombinedMinimumSize().Y);
        int remaining = Math.Max(0, available - firstMinimum - secondMinimum);
        Control.SizeFlags firstFlags = horizontal ? children[0].SizeFlagsHorizontal : children[0].SizeFlagsVertical;
        Control.SizeFlags secondFlags = horizontal ? children[1].SizeFlagsHorizontal : children[1].SizeFlagsVertical;
        bool firstExpands = (firstFlags & Control.SizeFlags.Expand) != 0;
        bool secondExpands = (secondFlags & Control.SizeFlags.Expand) != 0;
        if (firstExpands && secondExpands)
        {
            float firstRatio = Math.Max(0.001f, children[0].SizeFlagsStretchRatio);
            float secondRatio = Math.Max(0.001f, children[1].SizeFlagsStretchRatio);
            int weightedFirst = (int)Math.Round(available * firstRatio / (firstRatio + secondRatio));
            return Math.Clamp(weightedFirst, Math.Min(firstMinimum, available),
                Math.Max(Math.Min(firstMinimum, available), available - secondMinimum));
        }
        if (firstExpands) return firstMinimum + remaining;
        return firstMinimum;
    }
}
