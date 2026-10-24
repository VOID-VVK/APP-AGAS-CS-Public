#nullable enable

namespace AppAgasCs.Features.Tools.Execution;

/// <summary>Editable, local GDScript presets. Both use the same one-image tool contract.</summary>
public static class ToolPresetSources
{
    public static string Tile47StandardReverse { get; } = """
extends RefCounted

const TILE_SIZE := 64

func process(image: Image) -> Dictionary:
	if image == null or not is_instance_valid(image) or image.is_empty():
		return {"error": "Input image is empty."}
	if image.get_width() != 1024 or image.get_height() != 512:
		return {"error": "Input image must be exactly 1024x512 pixels (16x8 cells at 64 pixels per cell)."}
	var atlas := Image.create_empty(768, 256, false, Image.FORMAT_RGBA8)
	atlas.fill(Color(0, 0, 0, 0))
	# Original layout is a 16x8 sheet. Reconstruct its 12x4 standard atlas.
	_copy_tiles(image, atlas, Vector2i(0, 2), Vector2i(0, 0), Vector2i(4, 4))
	_copy_tiles(image, atlas, Vector2i(5, 2), Vector2i(4, 0), Vector2i(4, 4))
	_copy_tiles(image, atlas, Vector2i(11, 2), Vector2i(8, 0), Vector2i(4, 1))
	_copy_tile(image, atlas, Vector2i(11, 3), Vector2i(8, 1))
	_copy_tile(image, atlas, Vector2i(14, 3), Vector2i(11, 1))
	_copy_tile(image, atlas, Vector2i(11, 4), Vector2i(8, 2))
	_copy_tile(image, atlas, Vector2i(12, 4), Vector2i(9, 2))
	_copy_tile(image, atlas, Vector2i(14, 4), Vector2i(11, 2))
	_copy_tiles(image, atlas, Vector2i(11, 5), Vector2i(8, 3), Vector2i(4, 1))
	# Slot (10, 1) remains transparent.
	_copy_tile(image, atlas, Vector2i(8, 6), Vector2i(9, 1))
	_copy_tile(image, atlas, Vector2i(5, 6), Vector2i(10, 2))
	return {"image": atlas}

func _copy_tiles(source: Image, target: Image, source_cell: Vector2i, target_cell: Vector2i, size_cells: Vector2i) -> void:
	target.blit_rect(source, Rect2i(source_cell * TILE_SIZE, size_cells * TILE_SIZE), target_cell * TILE_SIZE)

func _copy_tile(source: Image, target: Image, source_cell: Vector2i, target_cell: Vector2i) -> void:
	_copy_tiles(source, target, source_cell, target_cell, Vector2i.ONE)
""";

    public static string Tile47Isometric { get; } = """
extends RefCounted

const MAX_DIMENSION := 2048
const MAX_PIXELS := 4194304
const EDGE_MARGIN := 20

func process(image: Image) -> Dictionary:
	if image == null or not is_instance_valid(image) or image.is_empty():
		return {"error": "Input image is empty."}
	var layout := _resolve_layout(image)
	if not bool(layout.get("ok", false)):
		return {"error": str(layout.get("error", "Unsupported Tile47 layout."))}
	var columns: int = layout["columns"]
	var rows: int = layout["rows"]
	var tiles: int = layout["tiles"]
	var cell_w: int = layout["cell_w"]
	var cell_h: int = layout["cell_h"]
	var origin_x: int = layout["origin_x"]
	var origin_y: int = layout["origin_y"]
	var angle := deg_to_rad(45.0)
	var cos_a := cos(angle)
	var sin_a := sin(angle)
	var y_scale := 0.5
	var max_x := (float(cell_w) + float(cell_h)) * cos_a * 0.5
	var max_y := (float(cell_w) + float(cell_h)) * sin_a * y_scale * 0.5
	var out_w: int = maxi(cell_w * 2, ceili(max_x * 2.0))
	var out_h: int = maxi(cell_h * 2, ceili(max_y * 2.0))
	out_w = mini(out_w, int(MAX_DIMENSION / columns))
	out_h = mini(out_h, int(MAX_DIMENSION / rows))
	var atlas_w := columns * out_w
	var atlas_h := rows * out_h
	if out_w < 1 or out_h < 1 or atlas_w > MAX_DIMENSION or atlas_h > MAX_DIMENSION or atlas_w * atlas_h > MAX_PIXELS:
		return {"error": "Output atlas exceeds the execution limit."}
	if max_x > out_w * 0.5 + 0.0001 or max_y > out_h * 0.5 + 0.0001:
		return {"error": "Transformed tile does not fit in the output cell."}
	var atlas := Image.create_empty(atlas_w, atlas_h, false, Image.FORMAT_RGBA8)
	atlas.fill(Color(0, 0, 0, 0))
	var src_cx := cell_w * 0.5
	var src_cy := cell_h * 0.5
	var out_cx := out_w * 0.5
	var out_cy := out_h * 0.5
	for tile_index in tiles:
		var col: int = tile_index % columns
		var row: int = int(tile_index / columns)
		var src_x0: int = origin_x + col * cell_w
		var src_y0: int = origin_y + row * cell_h
		var dst_x0: int = col * out_w
		var dst_y0: int = row * out_h
		for oy in out_h:
			for ox in out_w:
				var dx: float = (float(ox) + 0.5) - out_cx
				var dy: float = (float(oy) + 0.5) - out_cy
				var y_unscaled: float = dy / y_scale
				var sx: float = dx * cos_a + y_unscaled * sin_a + src_cx
				var sy: float = -dx * sin_a + y_unscaled * cos_a + src_cy
				var ix: int = floori(sx)
				var iy: int = floori(sy)
				if ix < 0 or iy < 0 or ix >= cell_w or iy >= cell_h:
					continue
				atlas.set_pixel(dst_x0 + ox, dst_y0 + oy, image.get_pixel(src_x0 + ix, src_y0 + iy))
	return {"image": atlas}

func _resolve_layout(image: Image) -> Dictionary:
	var width := image.get_width()
	var height := image.get_height()
	if width != height and width > EDGE_MARGIN * 2 and height > EDGE_MARGIN * 2:
		var inner := _detect_layout(width - EDGE_MARGIN * 2, height - EDGE_MARGIN * 2)
		if bool(inner.get("ok", false)):
			inner["origin_x"] = EDGE_MARGIN
			inner["origin_y"] = EDGE_MARGIN
			return inner
	var detected := _detect_layout(width, height)
	if bool(detected.get("ok", false)):
		return detected
	if width >= 12 * 64 and height >= 4 * 64:
		return _layout(12, 4, 47, 64, 64)
	return {"error": "Input image does not match Tile16, Tile47, or a 12x4 wall sheet."}

func _detect_layout(width: int, height: int) -> Dictionary:
	if width == height and width % 4 == 0:
		var cell := int(width / 4)
		if cell >= 1 and cell <= MAX_DIMENSION:
			return _layout(4, 4, 16, cell, cell)
	if width % 12 == 0 and height % 4 == 0:
		var cell_w := int(width / 12)
		var cell_h := int(height / 4)
		if cell_w >= 1 and cell_h >= 1 and cell_w <= MAX_DIMENSION and cell_h <= MAX_DIMENSION:
			return _layout(12, 4, 47, cell_w, cell_h)
	return {"ok": false}

func _layout(columns: int, rows: int, tiles: int, cell_w: int, cell_h: int) -> Dictionary:
	return {"ok": true, "columns": columns, "rows": rows, "tiles": tiles, "cell_w": cell_w, "cell_h": cell_h, "origin_x": 0, "origin_y": 0}
""";
}
