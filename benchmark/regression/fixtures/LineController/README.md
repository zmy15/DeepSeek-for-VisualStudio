# LineController Fixture

Dependency-free C++20 sample project used by the C++ regression suite.

```powershell
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Debug
cmake --build build
ctest --test-dir build --output-on-failure
```
