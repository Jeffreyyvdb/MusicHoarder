# Writes made-up widget data into the App Group folder, in the format the app writes.
import hashlib, json, os, shutil, sys, time
root = os.path.join(sys.argv[1], "widgets")
src = os.path.dirname(os.path.abspath(__file__))
os.makedirs(os.path.join(root, "art"), exist_ok=True)
def art(i):
    name = "art/" + hashlib.sha256(f"https://example.invalid/{i}".encode()).hexdigest() + ".jpg"
    shutil.copy(os.path.join(src, i + ".jpg"), os.path.join(root, name))
    return name
now = {"v": 1, "state": "track", "title": "Northern Lights", "artist": "Aurora Fields", "album": "Polar Nights",
       "art": art("np"), "reach": "background",
       "transport": {"playing": True, "position": 72, "duration": 214, "rate": 1.0, "asOf": time.time() * 1000,
                     "canNext": True, "canPrevious": True}}
albums = [("a1", "Glass Harbour", "The Tidewater Band"), ("a2", "Neon Orchard", "Mira Vale"),
          ("a3", "Low Sun", "Copperline"), ("a4", "Paper Planets", "Juno & the Static")]
shelf = {"v": 1, "items": [{"id": i, "title": t, "subtitle": s, "art": art(i), "path": f"/library?album={i}"} for i, t, s in albums]}
for name, doc in (("now-playing.json", now), ("shelf.json", shelf)):
    with open(os.path.join(root, name), "w") as f:
        json.dump(doc, f)
print("staged", sorted(os.listdir(root)), sorted(os.listdir(os.path.join(root, "art"))))
