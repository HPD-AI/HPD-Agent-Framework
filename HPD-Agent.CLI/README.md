# Simple Minecraft Voxel Game

A basic Minecraft-style voxel game built with Python and Ursina engine.

## Installation

1. Install Python 3.8 or higher
2. Install dependencies:
```bash
pip install -r requirements.txt
```

## How to Play

Run the game:
```bash
python voxel_game.py
```

## Controls

- **WASD** - Move around
- **Space** - Jump
- **Mouse** - Look around
- **Left Click** - Break block
- **Right Click** - Place block
- **Middle Click** - Pick block type (selects block you're looking at)
- **1-5 Keys** - Select block type:
  - 1: Grass (green)
  - 2: Dirt (brown)
  - 3: Stone (gray)
  - 4: Wood (brown)
  - 5: Leaves (dark green)
- **ESC** - Release mouse cursor

## Features

- Block placing and breaking
- 5 different block types
- First-person controller with movement and jumping
- Block type selection with visual preview
- Middle-click to copy block types
- Simple starting terrain

## Next Steps to Expand

- Add infinite terrain generation
- Implement crafting system
- Add inventory system
- Include different biomes
- Add water and lava
- Implement save/load functionality
- Add mobs and animals

## Notes

- The game starts with a small 8x8 terrain platform
- You can build anything from this starting point
- Use middle-click on any block to quickly select its type for building