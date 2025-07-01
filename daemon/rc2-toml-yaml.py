import tomllib
import argparse
import logging
import sys
import os
import importlib.util
from pprint import pformat

logging.basicConfig()
logger = logging.getLogger(__name__)

# Ensure that ruamel.yaml is installed
try:
    import ruamel.yaml
except ImportError as e:
    logger.error("ruamel.yaml python library not found, please install it!")
    sys.exit(1)

parser = argparse.ArgumentParser()

parser.add_argument("-i", "--input", type=argparse.FileType('r'), help="Input .toml file(s) to parse & convert", required=True, nargs="+")
parser.add_argument("-o", "--outfile", help="Output file to save YAML to")
parser.add_argument("-d", "--outdir", help="Output directory to save YAML files to")
parser.add_argument("-v", "--debug", help="Debug logging", action="store_true")

yaml = ruamel.yaml.YAML()
yaml.default_flow_style = False

def loadToml(tomlFile: str) -> dict | None:
    """
    Load a TOML RC2 config file into a dictionary
    """
    toml = {}
    with open(tomlFile, "rb") as f:
        toml = tomllib.load(f)

    # Print
    logger.info("Reading TOML data from %s" % tomlFile)
    logger.debug(pformat(toml))

    # Validate required keys
    for key in ['info', 'network', 'radio', 'audio', 'softkeys']:
        if key not in toml:
            logger.error("TOML config missing required section %s" % key)
            return None
        
    return toml
        
def saveYaml(toml: dict, outfile: str) -> bool:
    """
    Convert the loaded TOML data to a YAML config file and save
    """
    # Create YAML dict
    yaml_data = {
        "daemon": {
            "name": toml["info"]["name"],
            "desc": toml["info"]["desc"],
            "listenAddress": toml["network"]["ip"],
            "listenPort": toml["network"]["port"]
        },
        "control": {
            "controlMode": None,    # Will be set below
            "rxOnly": toml["radio"]["rxOnly"],
            "sb9600": {}            # Will be populated below
        },
        "audio": toml["audio"],
        "textLookups": {
            "zone": [],             # Will be populated below
            "channel": []           # Will be populated below
        },
        "softkeys": [],             # Will be populated below
    }

    # SB9600 Config
    if toml["radio"]["type"] == "sb9600":
        logger.info("Parsing SB9600 config")
        # Set control mode
        yaml_data["control"]["controlMode"] = 2
        # Ensure we have an SB9600 section
        if "sb9600" not in toml:
            logger.error("TOML configured for SB9600 but SB9600 section missing")
            return False
        sb9600 = toml["sb9600"]
        # Parse SB9600 config
        sb9600_yaml = {
            "serialPort": sb9600["port"],
            "controlHeadType": None,
            "softkeyBindings": {}
        }
        # Control head type
        if sb9600["head"] == "W9":
            sb9600_yaml["controlHeadType"] = 0
            logger.debug("Parsed SB9600 W9 head type")
        elif sb9600["head"] == "M3":
            sb9600_yaml["controlHeadType"] = 1
            logger.debug("Parsed SB9600 M3 head type")
        elif sb9600["head"] == "O5":
            sb9600_yaml["controlHeadType"] = 2
            logger.debug("Parsed SB9600 O5 head type")
        else:
            logger.error("Unable to parse SB9600 head type")
            return False
        # Optional Args
        if "useLedsForRx" in sb9600:
            sb9600_yaml["useLedsForRx"] = sb9600["useLedsForRx"]
        # Populate softkey bindings
        for btn_binding in toml["softkeys"]["buttonBinding"]:
            button = btn_binding[0]
            softkey = btn_binding[1]
            # Replace asterisk and pound with s and p
            button = button.replace("#", "p")
            button = button.replace("*", "s")
            sb9600_yaml["softkeyBindings"][button] = softkey if softkey else None
            logger.debug("Parsed SB9600 button binding %s: %s" % (button, softkey))
        # Add SB9600 yaml to main yaml
        yaml_data["control"]["sb9600"] = sb9600_yaml

    # Softkey Parsing
    if "softkeyList" not in toml["softkeys"]:
        logger.error("softkeyList not found in TOML file")
        return False
    else:
        yaml_data["softkeys"] = toml["softkeys"]["softkeyList"]
    
    # Text Lookup Parsing
    if "lookups" in toml:
        lookups = toml["lookups"]
        # Zone Lookups Parsing
        if "zoneLookup" in lookups:
            for zoneLookup in lookups["zoneLookup"]:
                yaml_data["textLookups"]["zone"].append({"match": zoneLookup[0], "replace": zoneLookup[1]})
                logger.debug("Parsed zone lookup '%s' -> '%s'" % (zoneLookup[0], zoneLookup[1]))
        # Channel Lookups Parsing
        if "chanLookup" in lookups:
            for chanLookup in lookups["chanLookup"]:
                yaml_data["textLookups"]["channel"].append({"match": chanLookup[0], "replace": chanLookup[1]})
                logger.debug("Parsed channel lookup '%s' -> '%s'" % (chanLookup[0], chanLookup[1]))

    # Save the YAML file
    with open(outfile, 'w') as output:
        yaml.dump(yaml_data, output)

    # Done!
    return True

if __name__ == "__main__":

    # Parse arguments
    args = parser.parse_args()

    # Setup logging
    if args.debug:
        logger.setLevel(logging.DEBUG)
        logger.debug("Debug logging enabled")
    else:
        logger.setLevel(logging.INFO)

    # Handle multiple files
    if len(args.input) > 1:
        # Outfile is invalid for multiple files
        if args.outfile:
            logger.error("Multiple input files specified, -o/--outfile is invalid")
            sys.exit(1)

    # Detect if any conversions failed
    failed = False
        
    # Iterate over input files
    for file in args.input:
        # Get output directory
        outname = os.path.splitext(os.path.basename(file.name))[0] + ".yml"
        outdir = os.path.dirname(file.name)
        # Override output directory
        if args.outdir:
            outdir = args.outdir
        outfile = os.path.join(outdir, outname)
        # Override output filename
        if args.outfile:
            outfile = args.outfile
        # Parse TOML
        toml = loadToml(file.name)
        if not toml:
            logger.error("Failed to decode TOML config file")
            sys.exit(1)
        # Save YAML
        if not saveYaml(toml, outfile):
            logger.error("Failed to convert %s to YAML" % file.name)
            failed = True
        else:
            logger.info("Converted %s to %s" % (file.name, outfile))

    if failed:
        logger.error("One or more conversions failed!")
        sys.exit(1)
    else:
        sys.exit(0)