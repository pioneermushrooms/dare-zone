"""Kept so `import shipgen` (graphics.py) still works: the generator lives in foundation_ships."""
import sys

from foundation_ships import shipgen as _shipgen

sys.modules[__name__] = _shipgen
