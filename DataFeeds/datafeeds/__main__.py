"""Entry point for ``python -m datafeeds``."""

from .cli import main

if __name__ == "__main__":
    raise SystemExit(main())