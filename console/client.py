# Logger
import logging

# HTTP server stuff
#import socketserver
import http.server
import ssl

# Argument Parser
import argparse

# Create logger object
logging.basicConfig()
logger = logging.getLogger(__name__)

# Argument parser
parser = argparse.ArgumentParser()

# Defaults
address = "127.0.0.1"
port = 4443
certfile = '../certs/localhost.crt'
keyfile = '../certs/localhost.key'
parser.add_argument("-a","--address", help="Server address to bind to")
parser.add_argument("-c","--cert", help="Web GUI certificate for TLS")
parser.add_argument("-k","--key", help="Web GUI keyfile (for TLS)")
parser.add_argument("-p","--port", help="Web GUI port")
parser.add_argument("-v","--verbose", help="Verbose logging", action="store_true")

def getArgs():
    """
    Get Arguments
    """
    global address
    global port
    global certfile
    global keyfile
    global logger

    # Parse the args
    args = parser.parse_args()

    # Set logging verbosity
    if args.verbose:
        logger.setLevel(logging.DEBUG)
    else:
        logger.setLevel(logging.INFO)

    # Make sure port and optionally an address were specified
    if not args.port:
        logger.error("No web GUI port specified!")
        exit(1)
    else:
        webguiport = int(args.port)
        if args.address:
            address = args.address

    # Make sure cert was specified
    if not args.cert:
        logger.error("No web GUI cert specified!")
        exit(1)
    else:
        certfile = args.cert

    # Make sure key was specified
    if not args.key:
        logger.error("No web GUI key specified!")
        exit(1)
    else:
        keyfile = args.key

class httpServerHandler(http.server.SimpleHTTPRequestHandler):
    """
    Main handler for https server hosting the web gui
    """

    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory="./", **kwargs)

    #def log_message(self, format, *args):
        """
        Surpress log messages for GET/POST requests
        """
    def end_headers(self):
        self.send_header('Access-Control-Allow-Origin', '*')
        http.server.SimpleHTTPRequestHandler.end_headers(self)

if __name__ == "__main__":
    """
    Main runtime
    """
    getArgs()

    # create TLS-secured HTTPServer
    logger.info("Starting web GUI server on address {}, port {}".format(address, port))
    httpServer = http.server.HTTPServer((address, port), httpServerHandler)
    httpServer.socket = ssl.wrap_socket(httpServer.socket,
                                    server_side=True,
                                    certfile=certfile,
                                    keyfile=keyfile,
                                    ssl_version=ssl.PROTOCOL_TLS)
    # start thread for HTTPS server
    try:
        httpServer.serve_forever()
    except KeyboardInterrupt:
        logger.info("Got keyboardinterrupt, shutting down")
        exit(0)