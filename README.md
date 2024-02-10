# RadioConsole2
The updated and improved python-radio-console!
## Overview
RC2 consists of two parts - the GUI console (`rc2-console`) and the radio control daemons (`rc2-daemon`), one per each radio to be connected to the console.
## Installation
### Daemon Installation
1. Download the appropriate `rc2-daemon` from the latest release, depending on the OS architecture you will be running the daemons on.
2. Install SDL2 - this is the library used for manipulating sound devices from the daemon and is not bundled by default. You will receive a runtime error if the library is not properly installed. Eventually I will figure out a better way to automate the installation of this library, but for now it has to be done manually.
   - **For Windows** - download the latest release of the [SDL2 library](https://github.com/libsdl-org/SDL) and place the `SDL2.dll` library in the same folder as your `daemon.exe`.
   - **For Linux** - install the latest version of SDL2 from your package manager (example - `apt install libsdl2-2.0-0` on Debian-based systems)
3. Test your `daemon` installation
   - Try to query your PC's audio devices by running `./daemon list-audio` from a command prompt/terminal. If everything is installed correctly, you should see a list of audio input & output devices printed to the terminal.
### Console GUI Installation
The `rc2-console` GUI is a self-contained electron application. Simple extract the exe to a location of your choosing and run.
## Configuration
### Configuring the radio control `daemon`
Use the `config.example.toml` file as a template for your daemon configuration.
## Building from Source
