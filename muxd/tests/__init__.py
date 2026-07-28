"""muxd test package.

This file is load-bearing on Windows dev boxes: a third-party `tests` package
installed into site-packages shadows this directory when it is only an implicit
namespace package, so `python -m unittest tests.test_*` resolves to the wrong
`tests` and every muxd verifier dies with ModuleNotFoundError. A regular package
at sys.path[0] wins outright.
"""
