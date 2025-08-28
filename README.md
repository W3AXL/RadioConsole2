# RadioConsole2
![](docs/media/screenshot-1.1.1-beta.2.png)

RadioConsole2 aims to be an open-source and expandable software console for controlling radios and radio systems remotely via WebRTC.

## Overview
RC2 consists of two parts - the GUI console (`rc2-console`) and the individual radio control daemons (`rc2-daemon`), one per each radio endpoint to be connected to the console. 

RC2 now also supports connection to a [DVMProject FNE](https://github.com/DVMProject/dvmhost) directly using the [rc2-dvm daemon](https://github.com/W3AXL/rc2-dvm).

RC2 is designed to be expandable and customizable, easy to develop for, and robust enough to serve as your main radio base station.

![](docs/media/screenshot-1.1.0-beta.1-lots.png)

## Documentation

All RC2 documentation is now hosted at [ReadTheDocs](https://rc2.readthedocs.io).

## Downloading

The latest release can be found on the right side of the Github Repo main page, or by [following this link](https://github.com/W3AXL/RadioConsole2/releases)

You will find both `rc2-daemon` and `rc2-console` available for download. You will need both for a full RC2 system, [see the documentation](https://rc2.readthedocs.io) for more information.

## Support

If you encounter issues using RC2, you should first consider joining [our Discord channel on the DVMProject discord server](https://discord.gg/Y9CF6zNtjr). If you don't wish to use
Discord, you can always [create an issue using our bug issue template](https://github.com/W3AXL/RadioConsole2/issues/new/choose).

## Development

See the development section of the documentatioion at [ReadTheDocs/Development](https://rc2.readthedocs.io/en/latest/development.html) for information on getting started developing for RC2