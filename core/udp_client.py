import socket


class UdpClient:

    def __init__(
        self, server_address: str = "<broadcast>", server_port: int = 21324
    ) -> None:
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.server_address = (server_address, server_port)

    def EnableBroadCastMode(self):
        self.socket.setsockopt(
            socket.SOL_SOCKET, socket.SO_BROADCAST, 1
        )  # Enable broadcast mode

    def SendData(self, data: bytearray):
        self.socket.sendto(data, self.server_address)
