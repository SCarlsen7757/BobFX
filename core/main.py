"""Main FastAPI app for countdown timer with UDP broadcast to ESP32 devices."""

import time
import asyncio
import socket
from contextlib import asynccontextmanager
from fastapi import FastAPI, Request, WebSocket, Body, WebSocketDisconnect
from fastapi.responses import HTMLResponse, FileResponse
from fastapi.staticfiles import StaticFiles
from fastapi.templating import Jinja2Templates
from .led_controller import IS_WS2812B_COMPATIBLE, LEDController
from .effect.rgb_effect import RGBEffectController


UDP_IP: str = "255.255.255.255"  # broadcast, or use fixed IPs
UDP_PORT: int = 4210
UDP_SEND: bool = False  # Set to True to enable UDP sending


@asynccontextmanager
async def lifespan(api: FastAPI):
    """App startup/shutdown events"""
    # Setup phase
    api.state.shutdown = False
    # Web setup
    api.mount("/static", StaticFiles(directory="core/static"), name="static")
    api.state.templates = Jinja2Templates(directory="core/templates")

    if IS_WS2812B_COMPATIBLE:
        api.state.led_controller = LEDController()
    else:
        api.state.led_controller = None

    api.state.end_time = 0.0
    api.state.running = False
    api.state.colors: list[str] = []  # type: ignore

    api.state.rgb_effect_controller: RGBEffectController = RGBEffectController(number_of_leds=256)  # type: ignore
    api.state.colors = None

    api.state.ws_led_clients: set[WebSocket] = set()  # type: ignore

    # UDP setup (broadcast to ESP32s)
    udp_socket: socket.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp_socket.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    api.state.udp_socket = udp_socket

    countdown_task = asyncio.create_task(cyclic_countdown_task())
    upd_task = asyncio.create_task(cyclic_udp_task())
    led_task = asyncio.create_task(cyclic_led_task())
    yield
    # Shutdown phase
    api.state.shutdown = True

    countdown_task.cancel()
    upd_task.cancel()
    led_task.cancel()

    try:
        await asyncio.gather(countdown_task, upd_task, led_task)
    except asyncio.CancelledError:
        pass

    udp_socket.close()


app = FastAPI(lifespan=lifespan)


@app.get("/", response_class=HTMLResponse)
async def home(request: Request):
    """Serve the main page"""
    return app.state.templates.TemplateResponse(
        "index.html", {"request": request, "countdown": format_time(remaining_time())}
    )


@app.get("/favicon.ico")
async def favicon():
    """Serve favicon"""
    return FileResponse("core/static/favicon.ico")


@app.post("/countdown/start")
async def start_countdown(minutes: int = Body(..., embed=True)):
    """Start a countdown for given minutes"""
    app.state.end_time = time.time() + (minutes * 60)
    app.state.running = True
    return {"status": "started"}


@app.post("/countdown/stop")
async def stop_countdown():
    """Stop the countdown"""
    app.state.running = False
    return {"status": "stopped"}


@app.get("/countdown/status")
async def get_status() -> dict[str, str]:
    """Get current countdown status"""
    return {"time": format_time(remaining_time()), "running": str(app.state.running)}


@app.post("/led/effect/next")
async def next_led_effect():
    """Switch to the next LED effect"""
    app.state.rgb_effect_controller.next_effect()
    return {"effect": app.state.rgb_effect_controller.current_effect.name}


@app.post("/led/effect/prev")
async def prev_led_effect():
    """Switch to the previous LED effect"""
    app.state.rgb_effect_controller.previous_effect()
    return {"effect": app.state.rgb_effect_controller.current_effect.name}


@app.post("/led/effect/color")
async def set_led_effect_color(color: str = Body(..., embed=True)):
    """Set the color for the current LED effect"""
    app.state.rgb_effect_controller.set_color(color)
    return {"color": color}


@app.websocket("/countdown/ws")
async def websocket_countdown_endpoint(websocket: WebSocket):
    """Send countdown updates over WebSocket"""
    await websocket.accept()

    try:
        while not app.state.shutdown:
            payload: dict[str, str] = {
                "countdown": format_time(remaining_time()),
                "running": str(app.state.running),
            }
            await websocket.send_json(payload)
            await asyncio.sleep(0.5)  # update interval
    except WebSocketDisconnect:
        pass


@app.websocket("/led/ws")
async def websocket_led_endpoint(websocket: WebSocket):
    """WebSocket endpoint to stream LED color data for animation"""
    await websocket.accept()
    app.state.ws_led_clients.add(websocket)
    if app.state.colors is not None:
        await websocket.send_json(app.state.colors)  # type: ignore
    while not app.state.shutdown:
        try:
            await asyncio.sleep(1)
        except WebSocketDisconnect:
            app.state.ws_led_clients.remove(websocket)


# ---------------- Helpers ----------------
def remaining_time() -> int:
    """Seconds left until countdown finishes"""
    if not app.state.running:
        return 0
    return max(0, int(app.state.end_time - time.time()))


def format_time(seconds: int) -> str:
    """Format seconds as MM:SS"""
    m, s = divmod(seconds, 60)
    return f"{m:02d}:{s:02d}"


# ---------------- Cyclic tasks ----------------
async def cyclic_countdown_task():
    """Main cyclic loop: check countdown + send UDP"""
    while True:
        await asyncio.sleep(1)

        seconds_left: int = remaining_time()
        if app.state.running:
            # When timer ends
            if seconds_left <= 0:
                app.state.running = False


async def cyclic_udp_task():
    """Cyclic task to manage UDP"""
    while UDP_SEND and not app.state.shutdown:
        await asyncio.sleep(1)
        # Broadcast status over UDP

        seconds_left: int = remaining_time()
        msg: str = str(seconds_left > 0)
        app.state.udp_socket.sendto(msg.encode(), (UDP_IP, UDP_PORT))


async def cyclic_led_task():
    """Cyclic task to manage LED"""
    while not app.state.shutdown:
        # Implement LED logic
        app.state.colors, update = app.state.rgb_effect_controller.update()
        if update:
            if app.state.led_controller:
                app.state.led_controller.update(app.state.colors)

            # Broadcast to all connected web socket clients
            for ws in set(app.state.ws_led_clients):
                try:
                    await ws.send_json(app.state.colors)
                except WebSocketDisconnect:
                    app.state.ws_led_clients.remove(ws)

        await asyncio.sleep(0.05)
