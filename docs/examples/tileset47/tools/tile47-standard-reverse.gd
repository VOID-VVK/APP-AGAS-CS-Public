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
