"""UDP Client module"""

import socket


class UdpClient:
    """UDP Client class"""

    def __init__(
        self, server_address: str = "<broadcast>", server_port: int = 21324
    ) -> None:
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.server_address = (server_address, server_port)

    def enable_broad_cast_mode(self):
        """Enable broad cast mode for this client"""
        self.socket.setsockopt(
            socket.SOL_SOCKET, socket.SO_BROADCAST, 1
        )  # Enable broadcast mode

    def send_data(self, data: bytearray):
        self.socket.sendto(data, self.server_address)

    def close(self):
        self.socket.close()
