---
title: "FlexView Plugin for Milestone XProtect"
description: "FlexView plugin for Milestone XProtect Smart Client. Design custom view layouts beyond standard templates with a free-form canvas."
---

<div class="show-title" markdown>

# Flex View

Design custom view layouts beyond the standard view templates. FlexView provides a canvas where you can freely create, resize, move, and arrange panes, then save the result as a standard XProtect view.

## Quick Start

1. Open the **Flex View** workspace tab in the Smart Client
2. Click and drag on the grid to create panes (minimum 2x2 cells)
3. Arrange and resize panes to your desired layout
4. Click **Save** to open the save dialog, enter a name, select a folder, and confirm

<video controls width="100%">
  <source src="../vids/flex_usage.mp4" type="video/mp4">
</video>

!!! info 
    Install it only where you need it. There is no security control to let users show or hide this plugin, so install it only on Smart Clients where you want to give users the ability to do this.

## Creating Panes

Click and drag on empty cells to create a new pane. A preview outline shows the pane dimensions while dragging. Release to place. The pane turns red if it would overlap an existing pane. Minimum pane size is 2x2 cells.

## Moving & Resizing

- **Move** - Click and drag a pane's body to reposition it
- **Resize** - Drag the bottom-right corner handle of a pane to resize it
- **Hover** - Panes highlight when hovered, showing the resize handle
- Cursor changes to indicate the available action (move, resize direction)
- Panes cannot overlap - invalid placements revert automatically

## Editing Existing Views

Click **Open View** to load an existing view. 
After confirming, the view picker opens. Use the **search field** at the top of the picker to filter views by name as you type.

When a view is loaded, its existing camera and built-in view-item assignments (Camera, Hotspot, Carousel, Matrix, HTML) are remembered. Camera names are shown in blue on each pane. After editing:

- Camera and built-in view-item assignments are restored to their original slot positions in the saved view.
- New panes start as empty slots.
- Removed panes are dropped from the layout.

## Saving Views

- **Save**: for a new view, opens the save dialog (name + folder). For an opened existing view, recreates the view in place under the same name and folder while restoring camera assignments.
- **Save As**: only available when an existing view is loaded. Creates a duplicate at a new name/folder, carrying over the camera assignments. After Save As, the editor switches to the new copy so subsequent saves target the duplicate, not the original.

The created view works like any standard XProtect view. Assign cameras to empty slots in the normal Smart Client view builder.

## Saving an Arrangement as a Layout

A **view** is a single saved screen with cameras in it. A **layout** is a reusable template - one of the shapes offered in Smart Client setup mode under **Add View**, next to the built-in 1x1 and 2x2 grids.

Click **Save as Layout** to store the current arrangement as one of those templates.

!!! warning "A layout stores the arrangement only"
    Layouts carry geometry and nothing else. Cameras and other view item content are not part of a layout, so a view created from one starts empty. If you want to keep the cameras, save a view instead.

The dialog asks for a name, an optional description, and which layout group to file it under, and previews exactly what will be stored. The panes are written at full precision - the arrangement you drew is the arrangement the template produces.

## Managing Layouts

Click **Manage Layouts** to list every layout defined on the site and delete the ones you no longer want. The list shows the owning group, pane count, last-modified date, and description.

!!! danger "Deleting a layout affects everyone"
    Layouts are shared server configuration, not personal settings. Deleting one removes it from **Add View** for every operator on the site, and it cannot be undone. Views already built from that layout keep working, because each view holds its own copy of the arrangement.

Both buttons write to the management server and need configuration rights that a standard operator account usually does not have. If either fails, the message names the reason and the full detail is written to `MIPLog.txt` with the `[FlexViewLayout]` prefix.

## Controls

| Action | How |
|---|---|
| **Create pane** | Click and drag on empty cells (min 2x2) |
| **Move pane** | Drag a pane's body |
| **Resize pane** | Drag the bottom-right corner handle |
| **Delete pane** | Right-click a pane, or select + Delete key |
| **New layout** | Click **New** to start fresh |
| **Open view** | Click **Open View**, confirm the recreate notice, pick a view (use the search field to filter) |
| **Save** | Save the current view - recreates an existing view with cameras restored |
| **Save As** | Duplicate an opened view to a new name/folder, carrying camera assignments |
| **Save as Layout** | Store the arrangement as a reusable template in **Add View**. Panes only, no cameras |
| **Manage Layouts** | List the site's layouts and delete them. Deletion is site-wide |
</div>
