"""Main FastAPI app for countdown timer with UDP WLED broadcast to ESP32 devices."""

import time
import asyncio
import os
from contextlib import asynccontextmanager
from fastapi import FastAPI, Request, WebSocket, Body, WebSocketDisconnect
from fastapi.responses import HTMLResponse, FileResponse
from fastapi.staticfiles import StaticFiles
from fastapi.templating import Jinja2Templates
from udp_client import UdpClient
from sound_player import SoundPlayer
from effect.rgb_effect import RGBEffectController, RGBEffect
from effect.utility import ConvertTuplesRGBToTupleHex
import settings


@asynccontextmanager
async def lifespan(api: FastAPI):
    """App startup/shutdown events"""
    # Setup phase
    api.state.shutdown = False
    # Web setup
    api.mount("/static", StaticFiles(directory="static"), name="static")
    api.state.templates = Jinja2Templates(directory="templates")

    api.state.end_time = 0.0
    api.state.running = False
    api.state.colors = tuple[(255, 255, 255)]

    api.state.rgb_effect_controller = RGBEffectController(
        number_of_leds=settings.NUMBER_OF_LEDS
    )

    api.state.ws_led_clients = set()

    udp_client: UdpClient = UdpClient(settings.UDP_IP, settings.UDP_PORT)
    udp_client.enable_broad_cast_mode()
    api.state.udp_client = udp_client

    # Sound setup
    sound_dir = os.path.abspath(
        os.path.join(os.path.dirname(__file__), "static", "sound")
    )
    api.state.sound_player = SoundPlayer(sound_dir)

    countdown_task = asyncio.create_task(cyclic_countdown_task())
    led_task = asyncio.create_task(cyclic_led_task())
    yield
    # Shutdown phase
    api.state.shutdown = True

    countdown_task.cancel()
    led_task.cancel()

    try:
        await asyncio.gather(countdown_task, led_task)
    except asyncio.CancelledError:
        pass

    api.state.udp_client.close()


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
    return FileResponse("static/favicon.ico")


@app.post("/countdown/start")
async def start_countdown(minutes: int = Body(..., embed=True)):
    """Start a countdown for given minutes"""

    await starting_sequence(minutes)

    app.state.sound_player.play_current_sound()
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
        await websocket.send_json(app.state.colors)
    while not app.state.shutdown:
        try:
            await asyncio.sleep(1)
        except WebSocketDisconnect:
            app.state.ws_led_clients.remove(websocket)


async def starting_sequence(minutes: int):
    """A simple starting sequence for the LEDs"""
    app.state.rgb_effect_controller.set_color("#0000FF")
    app.state.rgb_effect_controller.set_effect(RGBEffect.SCANNER)

    await asyncio.sleep(5)

    app.state.rgb_effect_controller.set_color("#00FF00")
    app.state.rgb_effect_controller.set_effect(RGBEffect.STROBE)
    app.state.running = True
    app.state.end_time = time.time() + (minutes * 60)

    await asyncio.sleep(2)

    app.state.rgb_effect_controller.set_effect(RGBEffect.BREATHING)


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


async def cyclic_led_task():
    """Cyclic task to manage LED"""

    while not app.state.shutdown:
        # Implement LED logic
        app.state.colors, update = app.state.rgb_effect_controller.update()
        if update:
            # Broadcast to all connected web socket clients
            for ws in set(app.state.ws_led_clients):
                try:
                    await ws.send_json(ConvertTuplesRGBToTupleHex(app.state.colors))
                except WebSocketDisconnect:
                    app.state.ws_led_clients.remove(ws)

            if settings.UDP_SEND:
                await send_led_data(app.state.colors)

        await asyncio.sleep(0.05)


async def send_led_data(colors: tuple[tuple[int, int, int], ...]) -> None:
    """Send LED data via UDP"""
    packet = bytearray([2, 2])  # Header for RGB [Mode, Timeout]
    for r, g, b in colors:
        packet.extend((r, g, b))

    app.state.udp_client.send_data(packet)
