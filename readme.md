# Reflector

A simple GPU stress and performance viewer for Linux. It renders a scene of
reflective, spinning metallic spheres and shows live FPS and frame times, so you
can quickly see how your GPU handles a load and compare GPUs on the same machine.

![Reflector demo](https://raw.githubusercontent.com/pradosh-arduino/Reflector/main/image.png)

## Features

- Live FPS counter and frame-time graph
- Adjustable load: sphere quality (low / medium / high), number of rings, and spheres per ring
- VSync toggle and FPS cap (0 = uncapped)
- Pick which GPU to render on from inside the app (useful on laptops with iGPU + dGPU)
- Orbit/zoom camera, pause, and borderless fullscreen
- Procedural metallic shading, so there are no asset files to load

## Requirements

**To run:**
- Linux x86-64 with OpenGL 3.3+ and working GPU drivers
- `pciutils` (`lspci`), used to list GPUs for the selector

**To build and install:**
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- `git`, `make`, and `sudo`

## Installation

```sh
git clone https://github.com/pradosh-arduino/Reflector.git
cd Reflector
make install
```

This publishes a self-contained single-file build, installs it to `/opt/reflector`,
symlinks it to `/usr/local/bin/reflector`, and adds a desktop entry. Run it with:

```sh
reflector
```

To change the install location: `make install PREFIX=/some/path`

## Usage

| Input | Action |
|---|---|
| Left mouse drag | Orbit camera |
| Mouse wheel | Zoom |
| `Space` | Pause / resume animation |
| `F11` | Toggle borderless fullscreen |

### Selecting a GPU

An OpenGL context is tied to one GPU when the window is created, so the GPU
can't be switched live. Choosing one in the **Select GPU** dropdown and clicking
**Apply GPU** restarts the app with the right PRIME offload environment:

- **NVIDIA:** `__NV_PRIME_RENDER_OFFLOAD=1` and `__GLX_VENDOR_LIBRARY_NAME=nvidia`
- **AMD / Intel:** `DRI_PRIME=pci-<bus id>`

The `GPU:` line at the top of the panel shows which renderer OpenGL is actually using.

## Makefile targets

| Command | Description |
|---|---|
| `make run` | Run in debug mode |
| `make build` | Debug build |
| `make publish` | Release build into `./publish` |
| `make install` | Publish and install system-wide |
| `make uninstall` | Remove the installed app |
| `make reinstall` | Uninstall, then install |
| `make clean` | Delete build output |

## Troubleshooting

- **GPU dropdown only shows "System default":** install `pciutils` (`sudo pacman -S pciutils` on Arch, `sudo apt install pciutils` on Debian/Ubuntu).
- **Selected GPU doesn't change the `GPU:` line:** check that your PRIME offload setup works for other apps (e.g. `glxinfo | grep renderer`).