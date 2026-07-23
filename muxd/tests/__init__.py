# Makes muxd/tests a regular package so `python -m unittest tests.test_*` resolves the
# repo's tests even when an unrelated site-packages `tests` package is installed
# (a regular package on a later sys.path entry otherwise beats a local namespace portion).
