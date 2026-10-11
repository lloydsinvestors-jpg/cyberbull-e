// Bully's rides as owned vehicles in Schedule I: Jimmy's BMX, the moped and (v0.13) the carnival go-kart.
//   * Bought at the carnival shop in the Chemical Plant warehouse (Shop.cs, Schedule I cash / bank); ownership saved in
//     UserData\BullyLink.rides.txt. Home: the parking spot nearest the motel, where owned rides wait when the world loads.
//   * R / pad Y next to one: get on / in. R / Y again: off (at speed on the bike: Bully's leap off).
//   * Handling straight from Bully's Config\handling.cfg (Austin, 2026-10-10: "fully implement rockstars bike riding and
//     car driving mechanics"): BIKE max 62 km/h, accel 38, brake 12.5, steering lock 50; MOPED 65 km/h, accel 9,
//     brake 7, lock 40; the go-kart is PHEONIX (model list "GoCart, Mower" lines up with handling "PHEONIX, WALTON"):
//     85 km/h, accel 50, brake 4.75, lock 21.8, traction 1.25 with 0.83 loss -> it slides. One model for all three:
//     a velocity vector with grip (side slip decays by grip), steering by the bicycle model (wheelbase, lock shrinking
//     with speed like Bully's assist, smoothed and rate-capped since v0.13.6).
//   * Bully's full bike moveset (Bikes.act / BIKE.HXD): pedal + sprint taps, coast, brake, skid (hard brake in a turn),
//     crouch-charged hop (low / medium / high), air tricks, back manual (wheelie) and front manual (stoppie) with
//     pedal nudges, punches + jabs at whoever's beside you, the slingshot + throwables from the saddle (Kit.RideFired),
//     walk-back, stand-turns, leap off, crashes over the bars / backwards / sideways.
//   * Own riding physics (Schedule I's skateboard can't be spawned from outside: v0.8.0's direct Mount() crashed the game).
//   * v0.16 (Austin: "the buggy bike machanics made me fall through the world, instead try to mirror gta5 bike
//     machanics"): until v0.15 the ride was a model slid along by hand, one ray down for the ground and a sphere cast
//     ahead for walls; sink more than ~1.2 m in a frame and the ray started under the ground and never found it again.
//     Now, like GTA V, the ride is a real Rigidbody (continuous collision): wheel spheres + a frame capsule, gravity and
//     collisions by the engine, held upright (rotation frozen; the lean is visual, as GTA's stabilised bikes look), its
//     origin ON the rear axle so it turns about the back wheel. Grip, speed and steering stay this file's (zero-friction
//     tyres). Crashes = speed physics took off us against something (the lost speed into it), pedestrians get knocked
//     flying instead of being walls, curbs get a lift, and a safety net puts the ride back on the last solid ground.
using System;
using System.Collections.Generic;
using System.IO;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.PlayerScripts;
using UnityEngine;

namespace BullyLink
{
    public class Vehicles
    {
        public enum Kind { Bike, Moped, Kart }

        class Ride
        {
            public Kind kind;
            public GameObject go;
            public Collider col;
            public float yaw, vy, lift, wheelAngle, crankAngle, wheelR, pitch, steerVis, rearBack, lean, wheelbase;
            public GameObject body;             // v0.16: the physics body while ridden (origin = ground under the rear axle)
            public Rigidbody rb;
            public readonly List<Collider> cols = new();               // the body's colliders (v0.22.5: kept to ignore trash)
            public Vector3 vel;                 // flat world velocity
            public List<Transform> wheels = new(), steerWheels = new();
            public Transform crank;
            public bool grounded = true;
        }

        public struct Handling
        {
            public string model, name; public float price, top, accel, brake, lockDeg, wheelbase, grip, lift, wheelR;
        }

        // top speed / accel / brake / lock from Bully's handling.cfg (km/h -> m/s; Bully's engine "acceleration" and
        // brake numbers are game units, kept as ratios between the three)
        public static readonly Dictionary<Kind, Handling> Spec = new()
        {
            // v0.13.6 (Austin: "the bike controlls ... are very loose"): handling.cfg's 62 is Bully's own unit, not real km/h;
            // a BMX tops out ~32 km/h on sprint taps and cruises ~22 km/h on the pedal
            [Kind.Bike] = new Handling { model = "bike", name = "Bike", price = 350f, top = 32f / 3.6f, accel = 3.0f, brake = 6.0f, lockDeg = 50f, wheelbase = 1.0f, grip = 10f, lift = 0.57f, wheelR = 0.38f },
            [Kind.Moped] = new Handling { model = "SCOOTER", name = "Moped", price = 1200f, top = 65f / 3.6f, accel = 2.2f, brake = 3.9f, lockDeg = 40f, wheelbase = 1.1f, grip = 9f, lift = 0f, wheelR = 0.22f },
            [Kind.Kart] = new Handling { model = "GoCart", name = "Go-Kart", price = 2500f, top = 85f / 3.6f, accel = 5.0f, brake = 2.7f, lockDeg = 21.8f, wheelbase = 1.26f, grip = 6.5f, lift = 0f, wheelR = 0.12f },
        };
        static readonly string SavePath = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "BullyLink.rides.txt");

        readonly HashSet<Kind> _owned = new();
        readonly Dictionary<Kind, Ride> _rides = new();
        Ride _riding;
        float _scan;
        Vector3? _home;
        public Action<string> Trace = _ => { };
        public Action<string, float> Say = (_, __) => { };
        public Kit Kit;                 // set by the mod once Bully mode has a kit: punches + the kit from the saddle
        public string Prompt;           // shown by the mod's status line
        public static bool Riding, NearRide;
        public static Transform RideRoot;                      // v0.22.17: the ridden model (the camera's collision check skips it)
        static readonly Quaternion Flip = Quaternion.Euler(0f, 180f, 0f);   // Bully models face +Y (backwards in Unity after conversion)
        PlayerMovement _mv;
        PlayerMovement Mv => _mv != null ? _mv : (_mv = UnityEngine.Object.FindObjectOfType<PlayerMovement>());
        public static string RidingKind;

        public Vehicles()
        {
            try { foreach (var l in File.ReadAllLines(SavePath)) if (Enum.TryParse<Kind>(l.Trim(), out var k)) _owned.Add(k); } catch { }
            Kit.RideFired = what => _fired = (what, Time.time);
        }

        void Save() { try { File.WriteAllLines(SavePath, System.Linq.Enumerable.Select(_owned, k => k.ToString())); } catch (Exception e) { Trace($"rides save: {e.Message}"); } }

        public void OnSceneChange() { _rides.Clear(); _riding = null; Riding = false; _home = null; _mv = null; _coast = null; }

        // ============================================================ ownership (the carnival shop sells them)
        public bool Owns(Kind k) => _owned.Contains(k);

        public void Grant(Kind k)
        {
            if (!_owned.Add(k)) return;
            Save();
            Trace($"rides: now own the {k}");
            SpawnHome(k);
        }

        // ============================================================ every frame
        AnimLayer _anim;

        public void Update(Player me, Transform jimmyRoot, AnimLayer anim, bool input = true)
        {
            _anim = anim;
            Prompt = null;
            NearRide = false;
            if (me == null) return;
            float now = Time.time;
            NoGrabWhileRiding(_riding != null);
            if (now > _scan) { _scan = now + 3f; Scan(me); RescueParked(); }
            if (_coast != null) Coast(Time.deltaTime);

            if (_riding != null) { Drive(me, jimmyRoot, anim); return; }
            if (!input) return;                    // native Schedule I controls / a game screen: rides stay parked

            foreach (var r in _rides.Values)
            {
                if (r.go == null) continue;
                if (Vector3.Distance(r.go.transform.position, me.transform.position) > 2.2f) continue;
                Prompt = $"R / Y: {(r.kind == Kind.Kart ? "drive" : "ride")} the {Spec[r.kind].name}";
                NearRide = true;
                if (BullyInput.KeyDown(BullyInput.VK_R) || BullyInput.PadDown(BullyInput.Y)) GetOn(me, r);
                return;
            }
        }

        // ============================================================ world: the motel, parking
        void Scan(Player me)
        {
            if (_home == null) _home = FindHome();
            foreach (var k in _owned)
                if (!_rides.TryGetValue(k, out var r) || r.go == null) SpawnHome(k);
        }

        Vector3? FindHome()
        {
            try
            {
                Vector3? motel = null;
                foreach (var p in Il2CppScheduleOne.Property.Property.Properties)
                {
                    string n = null;
                    try { n = p.PropertyName; } catch { }
                    if (n != null && n.IndexOf("motel", StringComparison.OrdinalIgnoreCase) >= 0) { motel = p.transform.position; break; }
                }
                if (motel == null) { Trace("rides: no motel property found"); return null; }
                Il2CppScheduleOne.Map.ParkingSpot best = null; float bd = 60f;
                foreach (var s in UnityEngine.Object.FindObjectsOfType<Il2CppScheduleOne.Map.ParkingSpot>())
                {
                    float d = Vector3.Distance(s.transform.position, motel.Value);
                    if (d < bd) { bd = d; best = s; }
                }
                var home = best != null ? best.transform.position : motel.Value;
                Trace($"rides: home = {(best != null ? $"parking spot {bd:F0} m from the motel" : "the motel itself")} {home}");
                return home;
            }
            catch (Exception e) { Trace($"rides home: {e.Message}"); return null; }
        }

        /// <summary>This ride's own parking spot (side by side at home), on the ground.</summary>
        // v0.22.28 (Austin: "make it where cars cant spawn on top of other vehicles"): the spot used to be taken blind, so a
        // car parked there got a ride dropped on / into it (and the ground ray could land on its roof). Now the ground ray
        // looks through vehicles, and the ride takes the first spot along its row (then the rows either side) with no car
        // or other ride in it.
        Vector3 HomeSpot(Kind k)
        {
            var baseAt = _home.Value + new Vector3(1.6f * (int)k, 0, 0);    // side by side
            Vector3 first = Ground(baseAt);
            foreach (float dz in new[] { 0f, 3.2f, -3.2f })
                for (int i = 0; i < 8; i++)
                {
                    var at = Ground(baseAt + new Vector3(1.6f * 3 * i, 0, dz));
                    if (!Occupied(at, k)) { if (i > 0 || dz != 0f) Trace($"rides: {k}'s spot is taken; parked {Vector3.Distance(first, at):F1} m along instead"); return at; }
                }
            Trace($"rides: no clear spot for the {k} near home; using its usual one");
            return first;
        }

        static bool IsVehicle(Collider c)
        {
            try { return c.GetComponentInParent<Il2CppScheduleOne.Vehicles.LandVehicle>() != null; } catch { return false; }
        }

        /// <summary>The ground at a spot, looking through any vehicle (never a car roof).</summary>
        Vector3 Ground(Vector3 at)
        {
            float best = float.MaxValue; Vector3 g = at;
            foreach (var h in Physics.RaycastAll(at + Vector3.up * 3f, Vector3.down, 8f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.collider == null || IsVehicle(h.collider) || OwnRide(h.collider)) continue;
                var rb = h.collider.attachedRigidbody;
                if (rb != null && !rb.isKinematic) continue;
                if (h.distance < best) { best = h.distance; g = h.point; }
            }
            return g;
        }

        bool OwnRide(Collider c)
        {
            foreach (var r in _rides.Values)
            {
                if (r.go != null && c.transform.IsChildOf(r.go.transform)) return true;
                if (r.body != null && c.transform.IsChildOf(r.body.transform)) return true;
            }
            return false;
        }

        /// <summary>A car, or another of our rides, in the space a ride parks in.</summary>
        bool Occupied(Vector3 at, Kind k)
        {
            foreach (var c in Physics.OverlapBox(at + Vector3.up * 0.9f, new Vector3(0.7f, 0.75f, 1.3f), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
            {
                if (c == null) continue;
                if (IsVehicle(c)) return true;
                foreach (var r in _rides.Values)
                    if (r.kind != k && r.go != null && c.transform.IsChildOf(r.go.transform)) return true;
            }
            return false;
        }

        /// <summary>A parked / rolling ride that's dropped under the map goes back to its spot (v0.22.24).</summary>
        void RescueParked()
        {
            if (_home == null) return;
            foreach (var r in _rides.Values)
            {
                if (r.go == null || r == _riding) continue;
                var pos = r.go.transform.position;
                if (pos.y > _home.Value.y - 25f) continue;
                if (_coast == r) _coast = null;
                DropBody(r);
                var spot = HomeSpot(r.kind);
                r.yaw = 0f; r.pitch = 0f;
                r.go.transform.SetPositionAndRotation(spot + Vector3.up * r.lift, Quaternion.Euler(0, 0, 0) * Flip);
                Trace($"rides: the parked {Spec[r.kind].name} was under the map at {pos}; back to its parking spot");
            }
        }

        void SpawnHome(Kind k)
        {
            if (_home == null) return;
            var at = HomeSpot(k);
            var sp = Spec[k];
            var go = PropLib.Make(sp.model);
            if (go == null) { Trace($"rides: no {sp.model} model"); return; }
            go.name = "BullyLink_" + sp.name;
            var r = new Ride { kind = k, go = go, lift = sp.lift, yaw = 0f, wheelR = sp.wheelR };
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith("spin_wheel")) { r.wheels.Add(t); if (t.name.StartsWith("spin_wheel_front")) r.steerWheels.Add(t); }   // v0.16: the bike's is "spin_wheel_front" (no _l / _r): it never steered and counted as a rear wheel
                else if (t.name == "spin_crank") r.crank = t;
            }
            go.transform.SetPositionAndRotation(at + Vector3.up * r.lift, Quaternion.Euler(0, r.yaw, 0) * Flip);
            // where the back axle sits behind the model's origin: the ride turns about it (v0.13.7)
            float rearSum = 0, frontSum = 0; int rearN = 0, frontN = 0; var f0 = Fwd(r);
            foreach (var w in r.wheels)
            {
                if (w == null) continue;
                float z = Vector3.Dot(w.position - go.transform.position, f0);
                if (r.steerWheels.Contains(w)) { frontSum += z; frontN++; } else { rearSum += z; rearN++; }
            }
            r.rearBack = rearN > 0 && rearSum / rearN < -0.1f ? -rearSum / rearN : sp.wheelbase * 0.5f;
            r.wheelbase = rearN > 0 && frontN > 0 && frontSum / frontN - rearSum / rearN > 0.4f ? frontSum / frontN - rearSum / rearN : sp.wheelbase;
            Trace($"rides: {k} rear axle {r.rearBack:F2} m behind the origin, wheelbase {r.wheelbase:F2} m ({rearN} rear / {frontN} front wheel(s) measured)");
            var box = go.AddComponent<BoxCollider>();
            box.center = new Vector3(0, (k == Kind.Kart ? 0.45f : 0.55f) - r.lift, 0);
            box.size = k == Kind.Bike ? new Vector3(0.5f, 1.1f, 1.8f) : k == Kind.Moped ? new Vector3(0.6f, 1.1f, 1.7f) : new Vector3(1.15f, 0.9f, 2.0f);
            r.col = box;
            _rides[k] = r;
            Trace($"rides: {k} parked at {at}");
        }

        // ============================================================ getting on / off
        float _onAt, _offAt = -1f;
        bool _followChecked, _leap;

        void GetOn(Player me, Ride r)
        {
            _riding = r;
            Riding = true;
            RidingKind = r.kind == Kind.Bike ? "bike" : r.kind == Kind.Moped ? "moped" : "kart";
            r.vel = Vector3.zero; r.vy = 0; r.pitch = 0; r.lean = 0;
            _onAt = Time.time; _followChecked = false; _crashAt = -1; _manual = 0; _charge = -1; _trickAt = -1;
            if (r.kind == Kind.Kart) SimRoad(me, r);                                     // v0.22.19: what's ahead that could kick it
            _mountFromRot = me.transform.rotation;                                           // v0.22.13: turn from here
            r.yaw = (r.go.transform.rotation * Quaternion.Inverse(Flip)).eulerAngles.y;
            if (r.col != null) r.col.enabled = false;
            if (_coast == r) _coast = null;
            MakeBody(me, r);
            try { var mv = Mv; if (mv != null) mv.CanMove = false; } catch { }
            Say(r.kind == Kind.Bike
                ? "Bike: W pedal (tap Shift sprint) · S brake / walk back · hold Space hop · X wheelie (+S stoppie) · Alt punch / air trick · click fire · R off"
                : r.kind == Kind.Kart
                ? "Go-kart: W gas · S brake / reverse · A/D steer · Space powerslide · T fist pump · R / Y out"
                : "Moped: W / RT throttle · S brake / reverse · A/D steer · R / Y off", 8f);
            Voice.Say("Jimmy", "PENTRIDE");
        }

        void GetOff(Player me, bool bailed = false, Vector3? landAt = null, bool rollOn = false)
        {
            var r = _riding;
            _riding = null;
            Riding = false;
            RideRoot = null;
            if (r == null) return;
            _offAt = Time.time;                                      // v0.22.13: LateFit slides him off the seat from _seatPos
            if (r.col != null) r.col.enabled = true;
            if (!rollOn) SetDown(r);                                       // v0.22.18: before the body goes (Destroy waits for the frame end)
            if (!rollOn) DropBody(r);                                      // a leapt-off bike keeps its body and rolls on (Coast)
            r.pitch = 0;
            r.go.transform.rotation = Quaternion.Euler(0, r.yaw, 0) * Flip;
            // v0.22.16 (Austin: "go kart stops mid air and stays there when player ejected from collision"): the body was
            // dropped wherever it was, so a crash mid-hop left the parked model hanging in the air. Set down on the ground.
            if (_anim != null && _anim.Playing != null && (_anim.Playing.StartsWith("bike_") || _anim.Playing.StartsWith("moped_") || _anim.Playing.StartsWith("kart_")))
            {
                if (bailed) _anim.Stop();
                else if (r.kind == Kind.Bike) _anim.Play("bike_off", fullBody: true);     // Bully's step-off, then back to walking
                else if (r.kind == Kind.Kart) _anim.Play("kart_off", fullBody: true);
                else _anim.Stop();
            }
            var side = landAt ?? r.go.transform.position - Vector3.up * r.lift + Quaternion.Euler(0, r.yaw, 0) * Vector3.left * (r.kind == Kind.Kart ? 1.3f : 1.0f);
            try
            {
                var mv = Mv;
                if (mv != null) { mv.CanMove = true; mv.Teleport(side, Quaternion.Euler(0, r.yaw, 0), true); }
            }
            catch (Exception e) { Trace($"rides off: {e.Message}"); }
            Voice.Say("Jimmy", "PEXRIDE");
            if (_engine != null) { _engine.Stop(); _engine = null; }
            if (_engineStarted) Sfx.Play(r.go.transform.position, 0.8f, r.kind == Kind.Kart ? "K1_EngStopSML16" : "VespaEngineOff16k");   // the engine dies as he gets off
            _engineStarted = false; _vfNow = 0f;
        }

        public void ForceOff(Player me) { if (_riding != null) GetOff(me); }

        // ============================================================ riding
        // bike move state
        float _pedalUntil, _charge = -1f, _manual, _manualStart, _trickAt = -1f, _crashAt = -1f, _turnAt = -1f, _punchAt = -1f;
        string _crashClip, _trickClip, _punchClip, _turnClip;
        bool _punchLeft;
        (string what, float at) _fired = ("", -1f);
        int _trick;

        void Drive(Player me, Transform jimmyRoot, AnimLayer anim)
        {
            var r = _riding;
            if (r.go == null) { _riding = null; Riding = false; RideRoot = null; return; }
            RideRoot = r.go.transform;
            float dt = Mathf.Min(Time.deltaTime, 0.05f), now = Time.time;
            var sp = Spec[r.kind];
            bool bike = r.kind == Kind.Bike, kart = r.kind == Kind.Kart;

            // a crash plays out Bully's fall, then Jimmy is off it
            if (_crashAt > 0)
            {
                r.vel = Vector3.MoveTowards(r.vel, Vector3.zero, 12f * dt);
                if (r.rb != null) { var cv = r.rb.velocity; r.rb.velocity = new Vector3(0f, cv.y, 0f); }
                Place(me, jimmyRoot, r, r.go.transform.position - Vector3.up * r.lift, Quaternion.Euler(0, r.yaw, 0), 0f);
                anim?.Play(_crashClip, holdEnd: true, fullBody: true, withRoot: true, rootPos: true);
                if (now - _crashAt > 0.9f) { _crashAt = -1; GetOff(me, bailed: true, landAt: _crashLand); }
                return;
            }
            bool climbing = now - _onAt < ClimbTime(r.kind);
            if (!climbing && (BullyInput.KeyDown(BullyInput.VK_R) || BullyInput.PadDown(BullyInput.Y)))
            {
                float fwdSpeed = Vector3.Dot(r.vel, Fwd(r));
                if (bike && fwdSpeed > 5f && r.grounded)
                {   // Bully: bail forward off a moving bike; it rolls on without you
                    anim?.Play("bike_leap", holdEnd: true, fullBody: true, withRoot: true, rootPos: true);
                    var land = r.go.transform.position - Vector3.up * r.lift + Fwd(r) * 2.0f;
                    if (Physics.Raycast(land + Vector3.up * 2f, Vector3.down, out var lg, 5f, ~0, QueryTriggerInteraction.Ignore)) land = lg.point;
                    Trace($"rides: leapt off the bike at {fwdSpeed * 3.6f:F0} km/h");
                    GetOff(me, bailed: true, landAt: land, rollOn: true);
                    _coast = r.rb != null ? r : null;
                    return;
                }
                GetOff(me); return;
            }

            // ---- input: Bully's ride layout (pad A accelerate, X brake / reverse, B jump, Y off; PC W / S / Space)
            float throttle = Mathf.Max(Mathf.Max(BullyInput.Key(0x57) ? 1f : 0f, BullyInput.StickY), BullyInput.Pad(BullyInput.A) && !bike ? 1f : 0f);
            float brake = Mathf.Max(Mathf.Max(BullyInput.Key(0x53) ? 1f : 0f, -BullyInput.StickY), BullyInput.Pad(BullyInput.X) ? 1f : 0f);
            float steer = Mathf.Clamp((BullyInput.Key(0x44) ? 1f : 0f) - (BullyInput.Key(0x41) ? 1f : 0f) + BullyInput.StickX, -1f, 1f);
            if (!bike) throttle = Mathf.Max(throttle, BullyInput.Trigger);
            bool sprint = bike && (BullyInput.KeyDown(BullyInput.VK_LSHIFT) || BullyInput.PadDown(BullyInput.A));
            bool hopHeld = BullyInput.Key(BullyInput.VK_SPACE) || BullyInput.Pad(BullyInput.B);
            bool manualHeld = bike && (BullyInput.Key(BullyInput.VK_X) || BullyInput.Pad(BullyInput.R3));
            bool handbrake = kart && hopHeld;
            if (climbing) { throttle = 0; brake = 0; sprint = false; hopHeld = false; manualHeld = false; }

            if (r.rb == null) MakeBody(me, r);
            IgnoreTrash(r);
            RunOver(me, r, now);
            var rb = r.rb;
            var fwd = Fwd(r);
            var right = Quaternion.Euler(0, r.yaw, 0) * Vector3.right;
            var vNow = rb.velocity;
            var hv = new Vector3(vNow.x, 0, vNow.z);

            // ---- what the world did to us since last frame (v0.16): the speed physics took off what we asked for, along
            // where we were going. A real hit, not a guess from a ray: walls stop the body, small stuff gets pushed, people
            // get knocked down, and only a big loss straight into something throws Jimmy off.
            float lost = _vSet.sqrMagnitude > 1f ? Vector3.Dot(_vSet - hv, _vSet.normalized) : 0f;
            if (lost > 2.2f && now - _onAt > 0.5f && now > _bounceUntil)
            {
                if (Hit(me, r, lost, now)) return;
                // v0.22.23: whatever the hit did to the body's velocity (a bounce, a curb hop, a knock) carries on from here;
                // this frame used to rebuild the velocity from before the hit and quietly undo it
                vNow = rb.velocity; hv = new Vector3(vNow.x, 0, vNow.z);
            }

            bool wasAir = !r.grounded;
            r.grounded = Grounded(me, r);
            if (wasAir && r.grounded) Landed(r, now, -_airVy);
            _airVy = vNow.y;
            if (r.grounded && now > _safeAt)
            {
                _safeAt = now + 0.5f; _safePos = rb.position; _safeYaw = r.yaw; _safeOk = true;
                _safeTrail.Add((rb.position, r.yaw)); if (_safeTrail.Count > 10) _safeTrail.RemoveAt(0);
            }

            float vf = Vector3.Dot(hv, fwd), vs = Vector3.Dot(hv, right);
            float top = sp.top;

            // ---- longitudinal
            if (bike)
            {
                // Bully's BMX: holding pedal cruises (~55% of top), sprint taps kick toward the top and fade back,
                // S brakes; stopped, holding S walks the bike back (BIKE_WALKBACK); coasting glides a long way
                float cruise = top * 0.68f;
                if (sprint) { vf = Mathf.Min(top, Mathf.Max(vf, cruise * 0.6f) + 1.6f); _pedalUntil = now + 0.8f; if (_manual > 0) _manualStart = now; }   // a tap in a manual = Bully's nudge
                if (throttle > 0.1f && vf < cruise) vf += sp.accel * throttle * dt;
                if (vf > cruise && now > _pedalUntil) vf = Mathf.MoveTowards(vf, cruise, 1.6f * dt);
                if (brake > 0.1f) vf = vf > 0.3f ? Mathf.Max(0, vf - sp.brake * brake * dt) : Mathf.MoveTowards(vf, -1.1f * brake, 3f * dt);
                if (throttle <= 0.1f && brake <= 0.1f) vf = Mathf.MoveTowards(vf, 0, 0.45f * dt);
            }
            else
            {
                if (throttle > 0.1f && vf < top) vf += sp.accel * throttle * dt * Mathf.Lerp(1f, 0.35f, Mathf.Clamp01(vf / top));   // power falls off toward the top
                if (brake > 0.1f) vf = vf > 0.2f ? Mathf.Max(0, vf - sp.brake * brake * dt) : Mathf.Max(kart ? -4f : -2f, vf - 2.5f * brake * dt);
                if (throttle <= 0.1f && brake <= 0.1f) vf = Mathf.MoveTowards(vf, 0, (kart ? 0.9f : 1.2f) * dt);
                if (handbrake) vf = Mathf.MoveTowards(vf, 0, 1.2f * dt);
            }

            // ---- steering: bicycle model, lock shrinking with speed (Bully's assist); stopped bike = stand-turns
            float speed01 = Mathf.Clamp01(Mathf.Abs(vf) / top);
            _steerS = Mathf.MoveTowards(_steerS, steer, (kart ? 3.5f : 4.5f) * dt);          // the bars turn, they don't snap
            if (Mathf.Abs(steer) < 0.05f) _steerS = Mathf.MoveTowards(_steerS, 0f, 3f * dt);   // and settle back straight
            float lockNow = sp.lockDeg * Mathf.Lerp(kart ? 1.0f : 0.6f, kart ? 0.25f : bike ? 0.12f : 0.15f, speed01) * (handbrake ? 1.6f : 1f);
            float yawRate = vf / sp.wheelbase * Mathf.Tan(_steerS * lockNow * Mathf.Deg2Rad) * Mathf.Rad2Deg;
            float maxRate = kart ? 120f : bike ? 100f : 90f;
            yawRate = Mathf.Clamp(yawRate, -maxRate, maxRate);
            if (!r.grounded) yawRate *= bike ? 0.35f : 0.1f;          // a little air steering on the BMX
            // v0.13.7 (Austin: "left and right slide it along a semicircle rather than the direction being at the turning
            // point of the handle bars"): it turned about its middle, so the back wheel swung out sideways on every tap.
            // Now it turns like a real bike (and GTA's / Bully's): the rear wheel is the pivot and only rolls forward,
            // the front wheel goes where the bars point, and the bars show the angle actually being steered.
            r.yaw += yawRate * dt;                     // the body's origin is the rear axle: it turns about the back wheel (v0.16)
            r.steerVis = Mathf.MoveTowards(r.steerVis, _steerS * lockNow, 160f * dt);
            if (bike && r.grounded && Mathf.Abs(vf) < 0.3f && Mathf.Abs(steer) > 0.5f && _turnAt < 0 && throttle < 0.1f)
            {   // BIKE_STDTURN_L / R: hop the stopped bike round
                _turnAt = now; _turnClip = steer < 0 ? "bike_stdturn_l" : "bike_stdturn_r"; _turnDir = Mathf.Sign(steer);
            }
            if (_turnAt > 0)
            {
                float u = (now - _turnAt) / 0.57f;
                if (u >= 1f) _turnAt = -1;
                else r.yaw += _turnDir * 75f * dt;
            }

            // ---- grip: side slip decays by grip; the kart's powerslide and the BMX's skid (hard brake in a turn) let go
            float grip = sp.grip;
            bool skid = bike && brake > 0.5f && Mathf.Abs(steer) > 0.4f && vf > 6f;
            if (handbrake && Mathf.Abs(vf) > 4f) grip = 1.4f;
            else if (skid) grip = 2.2f;
            else if (kart && Mathf.Abs(steer) > 0.8f && speed01 > 0.7f) grip = sp.grip * 0.83f;   // PHEONIX traction loss 0.83
            fwd = Fwd(r); right = Quaternion.Euler(0, r.yaw, 0) * Vector3.right;
            vs *= Mathf.Exp(-grip * dt);

            // ---- the bike's moves
            if (bike) BikeMoves(me, r, anim, now, dt, vf, hopHeld, manualHeld, brake);

            // ---- out to the body: our horizontal velocity on the ground (momentum in the air), the engine's vertical
            float vy = vNow.y;
            if (r.vy > 0f) { vy = r.vy; r.vy = 0f; r.grounded = false; _liftUntil = now + 0.4f; }   // a hop (BikeMoves)
            if (r.grounded) hv = fwd * vf + right * vs;
            if (!kart && r.grounded && vf > 1.2f && CurbAhead(me, r, fwd)) { vy = Mathf.Max(vy, kart ? 3.4f : 2.6f); _liftUntil = now + 0.3f; }   // GTA rides up curbs (the kart's low rails need more)
            // v0.22.16 (Austin: "look into how gta 5 does ... how it stops the car from bouncing off the road"): GTA's cars
            // ride on wheel contact, not on a bouncing collider: on the ground the body goes along the road's slope and is
            // held down onto it; it only leaves the road for a hop, a curb lift or the ground falling away (a crest / ramp).
            if (kart && now > _liftUntil && KartSuspension(me, r, hv, ref vy, now)) r.grounded = true;
            else if (!kart && r.grounded && now > _liftUntil && GroundUnder(me, r, out var gn, out float gap))
            {
                float follow = -(gn.x * hv.x + gn.z * hv.z) / Mathf.Max(0.35f, gn.y);           // up / down the slope, no more
                if (Launch(r, hv, follow, now, ref vy)) r.grounded = false;
                else if (vy > follow + 0.25f) vy = follow;                                    // no kick up off a seam or bump
                if (r.grounded && gap > 0.03f) vy = Mathf.Min(vy, follow - gap * 14f);        // pulled back down onto it
            }
            rb.velocity = new Vector3(hv.x, vy, hv.z);
            _wantYaw = r.yaw;                  // v0.17.1: applied in FixedStep (MoveRotation); setting rb.rotation here every frame
                                               // teleported the body and broke its interpolation: the rider shook
            _vSet = hv;
            r.vel = hv;
            _vfNow = vf;
            if (!bike) EngineSound(r, vf, throttle, top, now);
            // the safety net: somehow under the world anyway -> back on the last solid ground
            if (_safeOk && rb.position.y < _safePos.y - 25f && _home != null)
            {   // v0.22.24 (Austin: "make it when a vehicle goes under the map it respawns in it normal spot"): its parking spot
                var spot = HomeSpot(r.kind);
                Trace($"rides: {Spec[r.kind].name} fell {(_safePos.y - rb.position.y):F0} m under the map; back to its parking spot {spot}");
                rb.position = spot + Vector3.up * 0.5f; rb.velocity = Vector3.zero; r.yaw = 0f; _wantYaw = 0f; _vSet = Vector3.zero;
                _safePos = spot; _safeYaw = 0f; _safeTrail.Clear(); _rampVy = 0f; _bodyPitch = 0f;
            }
            else if (_safeOk && rb.position.y < _safePos.y - 25f)
            {
                // v0.22.12: the last saved spot can sit on the very edge of the hole (20:41-20:42: put back, fell again in 5 s).
                // Back along the trail of solid spots: the newest one 3 m+ from where it went under (twice in a row: 6 m+).
                var fellAt = rb.position;
                float need = now - _lastFallAt < 6f ? 6f : 3f;
                _lastFallAt = now;
                var back = (pos: _safePos, yaw: _safeYaw);
                for (int k = _safeTrail.Count - 1; k >= 0; k--)
                {
                    var e = _safeTrail[k]; var dd = e.pos - fellAt; dd.y = 0;
                    back = e;
                    if (dd.magnitude >= need) break;
                }
                Trace($"rides: fell {(_safePos.y - rb.position.y):F0} m below the last ground near {_safePos}; put back {need:F0} m+ back along the road at {back.pos}");
                rb.position = back.pos + Vector3.up * 0.5f; rb.velocity = Vector3.zero; r.yaw = back.yaw; _wantYaw = back.yaw; _vSet = Vector3.zero;
                _safePos = back.pos; _safeYaw = back.yaw;
            }
            var pos = r.body.transform.position + Fwd(r) * r.rearBack;      // the ground under the model's origin (interpolated)

            // ---- pitch from the wheels (+ the manuals), lean into turns
            // v0.17.1 (Austin: "the bike is stuck level even when you jump, the char model is shaking really bad"): the two
            // pitch rays hit the ground far below in the air (flat), and on the ground they started inside the new physics
            // body / the rider's capsule and flickered between them and the road. Ground: rays that skip both. Air: the
            // flight path, and like GTA you tip it: W noses down, S lifts the nose.
            float wbP = r.wheelbase > 0.3f ? r.wheelbase : sp.wheelbase;
            float pitchWant = 0f;
            if (r.grounded)
            {
                if (CastAway(me, r, pos + fwd * (wbP * 0.5f) + Vector3.up * 1.6f, 0.05f, Vector3.down, 3f, out var fh) &&
                    CastAway(me, r, pos - fwd * (wbP * 0.5f) + Vector3.up * 1.6f, 0.05f, Vector3.down, 3f, out var bh))
                    pitchWant = Mathf.Clamp(-Mathf.Atan2(fh.point.y - bh.point.y, wbP) * Mathf.Rad2Deg, -35f, 35f);
            }
            else
            {
                var vAir = rb.velocity;
                float traj = -Mathf.Atan2(vAir.y, Mathf.Max(new Vector3(vAir.x, 0, vAir.z).magnitude, 2f)) * Mathf.Rad2Deg;
                float tip = BullyInput.Key(0x57) ? 22f : BullyInput.Key(0x53) ? -22f : 0f;
                pitchWant = Mathf.Clamp(traj * 0.7f + tip, -45f, 45f);
            }
            _visPitch = Mathf.MoveTowards(_visPitch, pitchWant, (r.grounded ? 220f : 110f) * dt);
            float pitch = _visPitch;
            float manualPitch = _manual > 0 ? -24f : _manual < 0 ? 14f : 0f;   // wheelie: nose up; stoppie: nose down
            r.pitch = Mathf.MoveTowards(r.pitch, manualPitch, 90f * dt);
            // lean comes from the turn, not the key (it used to snap to the A/D key, which threw Jimmy sideways):
            // the angle that balances the turn, atan(speed * yaw rate / g), eased in and out
            float leanTarget = kart || !r.grounded ? 0f : -Mathf.Atan(vf * yawRate * Mathf.Deg2Rad / 9.81f) * Mathf.Rad2Deg;
            leanTarget = Mathf.Clamp(leanTarget, bike ? -35f : -30f, bike ? 35f : 30f);
            r.lean = Mathf.MoveTowards(r.lean, leanTarget, 70f * dt);
            float lean = r.lean;
            var full = Quaternion.Euler(pitch + r.pitch, r.yaw, lean);
            // a wheelie pivots on the rear hub, a stoppie on the front one
            var pivotOff = _manual > 0 ? -fwd * (sp.wheelbase * 0.5f) : _manual < 0 ? fwd * (sp.wheelbase * 0.5f) : Vector3.zero;
            var bodyPos = pos + pivotOff + Quaternion.Euler(r.pitch, r.yaw, 0) * Quaternion.Inverse(Quaternion.Euler(0, r.yaw, 0)) * -pivotOff;
            _squash = Mathf.MoveTowards(_squash, 0f, 0.5f * dt);
            r.go.transform.SetPositionAndRotation(bodyPos + full * (Vector3.up * r.lift) - Vector3.up * _squash, full * Flip);

            // ---- wheels roll (distance / radius), the kart's fronts steer, the crank turns with pedalling
            float roll = vf * dt / Mathf.Max(0.05f, r.wheelR) * Mathf.Rad2Deg;
            r.wheelAngle += roll;
            foreach (var w in r.wheels) if (w != null) w.localRotation = r.steerWheels.Contains(w) ? Quaternion.Euler(0, -r.steerVis, 0) * Quaternion.Euler(-r.wheelAngle, 0, 0) : Quaternion.Euler(-r.wheelAngle, 0, 0);

            Place(me, jimmyRoot, r, pos, Quaternion.Euler(0, r.yaw, 0), lean);
            jimmyRoot.rotation = full;
            if (anim != null) RiderPose(anim, r, top, throttle, brake, steer, vf, skid, handbrake, now);
            Prompt = $"{sp.name}  {Mathf.Abs(vf) * 3.6f:F0} km/h{(_manual > 0 ? "   WHEELIE" : _manual < 0 ? "   STOPPIE" : skid ? "   SKID" : handbrake && Mathf.Abs(vf) > 4f ? "   POWERSLIDE" : "")}   R / Y: off";
        }

        Vector3 Fwd(Ride r) => Quaternion.Euler(0, r.yaw, 0) * Vector3.forward;
        float _turnDir;
        Vector3 _crashLand;
        Ride _coast;            // a bike leapt off keeps rolling until it stops

        /// <summary>The player rides along (movement locked, carried with Teleport), Jimmy sits on it.</summary>
        // v0.22.13 (Austin: "make the getting in/on look really good"): no more snapping onto the seat. Getting on, Jimmy
        // glides over 0.3 s from where he stood (turning to face along the ride) while Bully's own climb-on clip plays;
        // getting off, he slides from the seat to where he steps down over 0.35 s (LateFit) instead of jumping there.
        Vector3 _seatPos; Quaternion _mountFromRot, _seatRot;
        const float GlideOn = 0.3f, GlideOff = 0.35f, RideGravity = 18f;
        /// <summary>How long the climb-in takes: its clip's own length (v0.22.15: the moped's 1.10 s clip was cut at 1.0 s).</summary>
        static float ClimbTime(Kind k)
        {
            var clip = k == Kind.Bike ? "bike_on" : k == Kind.Kart ? "kart_on" : "moped_on";
            return BullyAnims.Clips.TryGetValue(clip, out var c) ? c.Duration : 1f;
        }

        static float Ease(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }

        void Place(Player me, Transform jimmyRoot, Ride r, Vector3 pos, Quaternion rot, float lean)
        {
            float g = (Time.time - _onAt) / GlideOn;
            // v0.22.15: only the turn glides; the climb-in clip itself now carries him from beside the ride into the seat
            // (its root travel kept at export), so sliding his position here as well would move him twice
            if (g < 1f) rot = Quaternion.Slerp(_mountFromRot, rot, Ease(g));
            _seatPos = pos; _seatRot = rot;
            try { var mv = Mv; if (mv != null) { mv.CanMove = false; mv.Teleport(pos, rot, true); } } catch { }
            if (!_followChecked && Time.time - _onAt > 1.5f)
            {
                _followChecked = true;
                float lag = Vector3.Distance(me.transform.position, pos);
                Trace(lag > 1.5f ? $"rides: PLAYER NOT FOLLOWING the {Spec[r.kind].name} ({lag:F1} m behind): Teleport ignored while CanMove is off" : $"rides: player follows the {Spec[r.kind].name} ({lag:F2} m)");
            }
            jimmyRoot.SetPositionAndRotation(pos, rot);
        }

        void Crash(Ride r, Vector3 wallNormal, Vector3 pos)
        {
            var fwd = Fwd(r);
            float into = Vector3.Dot(-new Vector3(wallNormal.x, 0, wallNormal.z).normalized, fwd);
            _crashClip = r.kind == Kind.Kart ? "kart_crash" : into > 0.6f ? "bike_crash_front" : into < -0.2f ? "bike_crash_back" : "bike_crash_side";
            if (r.kind == Kind.Moped) _crashClip = "bike_crash_front";
            _crashAt = Time.time;
            _manual = 0;
            // where Jimmy ends up: thrown a little past the bars (front), or beside the ride
            _crashLand = pos + (into > 0.6f ? fwd * -0.3f + Vector3.Cross(Vector3.up, fwd) * 0.8f : Vector3.Cross(Vector3.up, fwd) * -1.0f);
            if (Physics.Raycast(_crashLand + Vector3.up * 2f, Vector3.down, out var g, 5f, ~0, QueryTriggerInteraction.Ignore)) _crashLand = g.point;
            r.vel = Vector3.zero;
            if (r.rb != null) r.rb.velocity = Vector3.zero;
            _vSet = Vector3.zero;
        }

        void Landed(Ride r, float now, float fall)
        {
            if (r.kind != Kind.Bike) return;
            if (_trickAt > 0 && now - _trickAt < 0.45f && fall > 3f)
            {   // still mid-trick on touchdown: Bully dumps you
                Trace("rides: landed mid-trick");
                Crash(r, -Fwd(r), r.go.transform.position - Vector3.up * r.lift);
                return;
            }
            if (_trickAt > 0) { Say($"Trick landed: {(_trick == 1 ? "no-hander" : _trick == 2 ? "can-can" : "superman seat grab")}", 2.5f); Voice.Say("Jimmy", "PJEER"); }
            _trickAt = -1;
            if (fall > 4f) _landUntil = now + 0.35f;
        }

        float _landUntil, _steerS, _bumpAt;
        const float CrankMetresPerRev = 2f * Mathf.PI * 0.38f * 2.75f;   // ~6.6 m of road per crank turn

        /// <summary>Bully's BMX moveset on top of the riding (Bikes.act: Ground/Crouch/HOP Low-Medium-High, Air/Tricks,
        /// Ground/Manual Back / Front + ManualNudge, Attacks Punch / Jab, the kit from the saddle).</summary>
        void BikeMoves(Player me, Ride r, AnimLayer anim, float now, float dt, float vf, bool hopHeld, bool manualHeld, float brake)
        {
            // hop: hold to crouch + charge, release to jump; longer charge, higher hop
            if (r.grounded && _manual == 0)
            {
                if (hopHeld && _charge < 0) _charge = now;
                if (!hopHeld && _charge > 0)
                {
                    float held = now - _charge; _charge = -1;
                    r.vy = held < 0.25f ? 3.8f : held < 0.6f ? 4.8f : 6.0f;
                    r.grounded = false;
                    _hopHigh = held >= 0.6f;
                }
            }
            else if (!hopHeld) _charge = -1;
            // air tricks: Alt / pad X in the air (Bully: Air/Tricks), one per jump
            if (!r.grounded && _trickAt < 0 && r.vy > -6f && (BullyInput.MeleeDown || BullyInput.PadDown(BullyInput.X)))
            {
                _trick = _trick % 3 + 1; _trickAt = now; _trickClip = "bike_trick" + _trick;
            }
            // manuals: X held while rolling = back manual (wheelie); with the brake on = front manual (stoppie). They
            // hold ~2.5 s, a pedal tap (sprint) nudges them on (Bully's ManualNudge); too slow and they drop.
            if (r.grounded && manualHeld && Mathf.Abs(vf) > 2.5f)
            {
                int want = brake > 0.3f ? -1 : 1;
                if (_manual != want) { _manual = want; _manualStart = now; }
                if (now - _manualStart > 2.5f) { _manual = 0; Say("Lost the manual - tap Shift / A to nudge it on", 2f); }
            }
            else _manual = 0;
            // punches: Alt / click with fists (Bully: Punch / Jab, L or R toward whoever's closer); jab when slow
            bool punch = (BullyInput.MeleeDown || (Kit != null && Kit.Selected == Item.Fists && BullyInput.FireDown)) && r.grounded && _manual == 0;
            if (punch && now - _punchAt > 0.45f && Kit != null)
            {
                var fwd = Fwd(r); var rightV = Quaternion.Euler(0, r.yaw, 0) * Vector3.right;
                bool jab = Mathf.Abs(vf) < 2f;
                var at = me.transform.position;
                NPC hitL = null, hitR = null;
                // try the side the last swing didn't go to first (Bully alternates when nobody's there)
                _punchLeft = !_punchLeft;
                if (_punchLeft) { hitL = Kit.RideHit(me, at, -rightV, 1.7f, jab); if (hitL == null) hitR = Kit.RideHit(me, at, rightV, 1.7f, jab); }
                else { hitR = Kit.RideHit(me, at, rightV, 1.7f, jab); if (hitR == null) hitL = Kit.RideHit(me, at, -rightV, 1.7f, jab); }
                bool left = hitL != null || (hitR == null && _punchLeft);
                _punchClip = (jab ? "bike_jab_" : "bike_punch_") + (left ? "l" : "r");
                _punchAt = now;
            }
        }

        bool _hopHigh;

        /// <summary>Bully's riding states (clips picked from tools/strip_sheet.py time strips ON the vehicle).</summary>
        void RiderPose(AnimLayer anim, Ride r, float top, float throttle, float brake, float steer, float vf, bool skid, bool handbrake, float now)
        {
            const bool F = true;
            float spd = Mathf.Abs(vf);
            if (now - _onAt < ClimbTime(r.kind))
            { anim.Play(r.kind == Kind.Bike ? "bike_on" : r.kind == Kind.Kart ? "kart_on" : "moped_on", holdEnd: true, fullBody: F, withRoot: F, rootPos: F); return; }
            if (r.kind == Kind.Kart)
            {
                if (BullyInput.TauntDown) _cheerUntil = now + 1.5f;
                if (now < _cheerUntil) { anim.Play("kart_cheer", fullBody: F, withRoot: F, rootPos: F); return; }
                if (Mathf.Abs(steer) > 0.8f && spd > top * 0.4f || handbrake && spd > 4f) { anim.Play(steer < 0 ? "kart_hard_l" : "kart_hard_r", holdEnd: true, fullBody: F, withRoot: F, rootPos: F); return; }
                if (Mathf.Abs(steer) > 0.3f) { anim.Play(steer < 0 ? "kart_left" : "kart_right", holdEnd: true, fullBody: F, withRoot: F, rootPos: F); return; }
                anim.Play("kart_idle", freeze: 0f, fullBody: F, withRoot: F, rootPos: F);
                return;
            }
            if (r.kind == Kind.Moped)
            {
                if (spd < 0.3f && throttle < 0.1f) anim.Play("moped_stop", freeze: 0f, fullBody: F, withRoot: F, rootPos: F);
                else anim.Play("moped_ride", freeze: 0f, fullBody: F, withRoot: F, rootPos: F);
                return;
            }
            // ---- the BMX, most specific state first
            if (now - _punchAt < 0.5f && _punchClip != null) { anim.Play(_punchClip, holdEnd: true, fullBody: F, withRoot: F, rootPos: F); return; }
            if (now - _fired.at < 0.45f) { anim.Play(_fired.what == "sling" ? "bike_sling" : "bike_throw", holdEnd: true, fullBody: F, withRoot: F, rootPos: F); return; }
            if (_turnAt > 0) { anim.Play(_turnClip, holdEnd: true, fullBody: F, withRoot: F, rootPos: F); return; }
            if (!r.grounded)
            {
                if (_trickAt > 0) { anim.Play(_trickClip, holdEnd: true, fullBody: F, withRoot: F, rootPos: F); return; }
                anim.Play(_hopHigh ? "bike_hop_high" : "bike_hop", holdEnd: true, fullBody: F, withRoot: F, rootPos: F); return;
            }
            if (now < _landUntil) { anim.Play("bike_land", holdEnd: true, fullBody: F, withRoot: F, rootPos: F); return; }
            if (_manual > 0) { anim.Play("bike_bmanual", fullBody: F, withRoot: F, rootPos: F); return; }
            if (_manual < 0) { anim.Play("bike_fmanual", fullBody: F, withRoot: F, rootPos: F); return; }
            if (_charge > 0) { anim.Play("bike_crouch", fullBody: F, withRoot: F, rootPos: F); return; }
            if (vf < -0.2f) { anim.Play("bike_walkback", fullBody: F, withRoot: F, rootPos: F); return; }
            if (spd < 0.3f && throttle < 0.1f) { anim.Play("bike_stop", freeze: 0f, fullBody: F, withRoot: F, rootPos: F); return; }
            // the pedal stroke (V_Bike #1: 0.41 m of foot travel, feet in perfect opposition) at the cadence the wheels ask
            // for: one 1.33 s clip = one crank turn (v0.13 played it at a fixed, lazy rate)
            float cadence = Mathf.Clamp(spd / CrankMetresPerRev * 1.33f, 0.45f, 2.6f);
            if (now < _pedalUntil) { anim.Play("bike_sprint", fullBody: F, withRoot: F, rootPos: F); anim.SetSpeed(Mathf.Max(1.2f, cadence * 1.1f)); return; }
            if (throttle > 0.1f) { anim.Play("bike_pedal", fullBody: F, withRoot: F, rootPos: F); anim.SetSpeed(cadence); return; }
            anim.Play("bike_coast", fullBody: F, withRoot: F, rootPos: F);      // coasting: Bully's crouched glide
        }

        float _cheerUntil;

        /// <summary>A bike you leapt off rolls on and stops (called every frame from Update while nobody rides it).</summary>
        void Coast(float dt)
        {
            var r = _coast;
            if (r == null || r.go == null || _riding == r) { _coast = null; return; }
            if (r.rb == null) { _coast = null; return; }
            var v = r.rb.velocity; var hv = new Vector3(v.x, 0, v.z);
            if (hv.magnitude < 0.1f)
            {
                var ground = r.body.transform.position + Fwd(r) * r.rearBack;
                DropBody(r);
                _coast = null;
                r.go.transform.SetPositionAndRotation(ground + Vector3.up * r.lift, Quaternion.Euler(0, r.yaw, 80f) * Flip);   // tips over
                return;
            }
            hv = Vector3.MoveTowards(hv, Vector3.zero, 3f * dt);
            r.rb.velocity = new Vector3(hv.x, v.y, hv.z);
            r.go.transform.SetPositionAndRotation(r.body.transform.position + Fwd(r) * r.rearBack + Vector3.up * r.lift, Quaternion.Euler(0, r.yaw, 0) * Flip);
        }

        // ============================================================ the physics body (v0.16, GTA V style)
        Vector3 _vSet, _safePos; float _safeYaw, _safeAt, _airVy, _wantYaw, _visPitch, _lastFallAt = -99f; bool _safeOk;
        readonly List<(Vector3 pos, float yaw)> _safeTrail = new();          // the last 10 solid spots, 0.5 s apart

        /// <summary>Mod.OnFixedUpdate: the steering's heading goes in through the physics step (smooth, interpolated).</summary>
        public void FixedStep()
        {
            var r = _riding;
            if (r?.rb == null || _crashAt > 0) return;
            // v0.22.24: the kart's body lies along the road (level for the bike / moped, whose wheels are spheres)
            float pitchTo = r.kind == Kind.Kart && r.grounded ? _groundPitch : r.kind == Kind.Kart ? _bodyPitch : 0f;
            _bodyPitch = Mathf.MoveTowards(_bodyPitch, pitchTo, 90f * Time.fixedDeltaTime);
            r.rb.MoveRotation(Quaternion.Euler(_bodyPitch, _wantYaw, 0f));
            // v0.22.13 (Austin: "the vehicles gravity needs fixing too"): the body fell at Unity's 9.81, but the hops, curb
            // lifts and the old ride model were tuned for Bully's 18 m/s2: everything floated. Extra downforce up to 18.
            float gNow = Mathf.Abs(Physics.gravity.y);
            if (gNow > 0.5f) r.rb.AddForce(Vector3.down * (RideGravity - gNow), ForceMode.Acceleration);
        }

        // ---- pedals locked to Jimmy's feet (v0.17.1, Austin: "the pedels are inverted where the right foot is up but the
        // pedle is down"): the crank used to spin on its own counter. Now, after Jimmy's pose is final, it's turned so its
        // right pedal points at his right foot; the left follows (one rigid part).
        Transform _rFoot; Transform _footRoot;
        Vector3 _pedalRest; bool _pedalKnown;

        /// <summary>Call after Jimmy's animation layer has posed him (Mod.OnLateUpdate).</summary>
        // ---- v0.22.20 (Austin: "his head is stuck looking back make it look forward and look back when hes in revurse"):
        // reversing, Jimmy turns his head over his right shoulder (neck 30 + head 45 deg about his up), eased in and out
        float _vfNow, _look;
        Transform _lookRoot, _neck, _head;
        void LookBack(Transform jimmyRoot)
        {
            if (_lookRoot != jimmyRoot)
            {
                _lookRoot = jimmyRoot; _neck = _head = null;
                foreach (var t in jimmyRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "Root Neck") _neck = t;
                    else if (t.name == "Root Head") _head = t;
                }
            }
            _look = Mathf.MoveTowards(_look, _riding != null && _vfNow < -0.4f ? 1f : 0f, 3f * Time.deltaTime);
            if (_look < 0.01f || _neck == null || _head == null) return;
            float e = Ease(_look);
            var up = jimmyRoot.up;
            _neck.rotation = Quaternion.AngleAxis(30f * e, up) * _neck.rotation;
            _head.rotation = Quaternion.AngleAxis(45f * e, up) * _head.rotation;
        }

        // ---- v0.22.20 (Austin: "give sound effects to the moped and go cart for their engines"): Bully's own engine
        // sounds. The engine starts as he finishes climbing in, idles, and runs its drive loop with pitch rising with
        // speed (the kart changes up to its fast loop past ~55% of top speed); it dies with Bully's stop sound on getting off.
        Sfx.Loop _engine;
        bool _engineStarted;
        float _engineStartAt;
        void EngineSound(Ride r, float vf, float throttle, float top, float now)
        {
            bool kart = r.kind == Kind.Kart;
            if (!_engineStarted)
            {
                if (now - _onAt < ClimbTime(r.kind) - 0.2f) return;
                _engineStarted = true; _engineStartAt = now;
                Sfx.Play(r.go.transform.position, 0.9f, kart ? "K1_EngStartSML" : "VespaEnigineStart");
                return;
            }
            if (now - _engineStartAt < 0.6f) return;                        // the start sound plays out
            if (_engine == null || !_engine.Alive) _engine = new Sfx.Loop(r.go.transform);
            float s = Mathf.Clamp01(Mathf.Abs(vf) / Mathf.Max(1f, top));
            string idle = kart ? "K1_EngIdle" : "VespaIdleSML2";
            if (s < 0.08f && throttle < 0.1f) { _engine.Set(idle, 0.55f, 1f); return; }
            if (kart)
            {
                if (s < 0.55f) _engine.Set("K1_EngMedEQ", 0.6f + 0.2f * throttle, 0.8f + s / 0.55f * 0.35f);
                else _engine.Set("K1_EngFstEQ", 0.65f + 0.2f * throttle, 0.9f + (s - 0.55f) / 0.45f * 0.25f);
            }
            else _engine.Set("VespaEngine", 0.55f + 0.2f * throttle, 0.75f + s * 0.55f + throttle * 0.05f);
        }

        public void LateFit(Transform jimmyRoot)
        {
            if (jimmyRoot != null) LookBack(jimmyRoot);
            if (_riding == null && jimmyRoot != null && _offAt > 0 && Time.time - _offAt < GlideOff)
            {
                float e = Ease((Time.time - _offAt) / GlideOff);
                jimmyRoot.SetPositionAndRotation(Vector3.Lerp(_seatPos, jimmyRoot.position, e), Quaternion.Slerp(_seatRot, jimmyRoot.rotation, e));
            }
            var r = _riding;
            if (r == null || r.kind != Kind.Bike || r.crank == null || r.crank.parent == null || jimmyRoot == null) return;
            if (_footRoot != jimmyRoot) { _footRoot = jimmyRoot; _rFoot = null; foreach (var t in jimmyRoot.GetComponentsInChildren<Transform>(true)) if (t.name == "Root R Foot") { _rFoot = t; break; } }
            if (_rFoot == null) return;
            var par = r.crank.parent;
            var axis = par.TransformDirection(Vector3.right).normalized;        // the crank turns about its local x
            if (!_pedalKnown) LearnPedal(r);
            if (!_pedalKnown) return;
            var hub = r.crank.position;
            var df = Vector3.ProjectOnPlane(_rFoot.position - hub, axis);
            var d0 = Vector3.ProjectOnPlane(par.TransformDirection(_pedalRest), axis);
            if (df.sqrMagnitude < 1e-6f || d0.sqrMagnitude < 1e-6f) return;
            r.crank.localRotation = Quaternion.AngleAxis(Vector3.SignedAngle(d0, df, axis), Vector3.right);
        }

        /// <summary>Where the crank's RIGHT pedal points at rest: the far-from-hub vertices on the bike's right side.</summary>
        void LearnPedal(Ride r)
        {
            try
            {
                var mf = r.crank.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) return;
                var v = mf.sharedMesh.vertices;
                var par = r.crank.parent;
                var bikeRight = Quaternion.Euler(0, r.yaw, 0) * Vector3.right;
                float sideSign = Mathf.Sign(Vector3.Dot(par.TransformDirection(Vector3.right), bikeRight));
                float maxR = 0f;
                foreach (var p in v) if (p.x * sideSign > 0.02f) maxR = Mathf.Max(maxR, new Vector2(p.y, p.z).magnitude);
                if (maxR < 0.05f) return;
                Vector3 sum = Vector3.zero; int n = 0;
                foreach (var p in v) if (p.x * sideSign > 0.02f && new Vector2(p.y, p.z).magnitude > maxR * 0.7f) { sum += new Vector3(0f, p.y, p.z); n++; }
                if (n == 0) return;
                _pedalRest = sum / n;
                _pedalKnown = true;
                Trace($"rides: right pedal at rest {_pedalRest} ({n} vertices, crank radius {maxR:F2} m); pedals follow Jimmy's feet");
            }
            catch (Exception e) { Trace($"rides: crank: {e.Message}"); _pedalKnown = true; _pedalRest = Vector3.zero; }
        }
        static PhysicMaterial _slick;

        void MakeBody(Player me, Ride r)
        {
            DropBody(r);
            var sp = Spec[r.kind];
            bool kart = r.kind == Kind.Kart;
            float wb = r.wheelbase > 0.3f ? r.wheelbase : sp.wheelbase;
            var go = new GameObject("BullyLink_RideBody");
            var groundAtOrigin = r.go.transform.position - Vector3.up * r.lift;
            go.transform.SetPositionAndRotation(groundAtOrigin - Fwd(r) * r.rearBack + Vector3.up * 0.03f, Quaternion.Euler(0f, r.yaw, 0f));
            _slick ??= new PhysicMaterial { name = "BullyLink_Tyres", dynamicFriction = 0f, staticFriction = 0f, bounciness = 0f, frictionCombine = PhysicMaterialCombine.Minimum, bounceCombine = PhysicMaterialCombine.Minimum };
            var cols = new List<Collider>();
            if (kart)
            {   // two low rails (rolls over small lips like the bike's wheels) + the tub
                foreach (float x in new[] { -0.45f, 0.45f })
                {
                    // v0.22.19: lifted 0.2 m clear of the road; the kart rides on KartSuspension's four probes, and the rails
                    // and tub only meet walls, curbs too tall to climb and other cars
                    var c = go.AddComponent<CapsuleCollider>(); c.direction = 2; c.radius = 0.2f; c.height = wb + 0.4f; c.center = new Vector3(x, 0.4f, wb * 0.5f); cols.Add(c);
                }
                var tub = go.AddComponent<BoxCollider>(); tub.size = new Vector3(1.0f, 0.4f, wb + 0.5f); tub.center = new Vector3(0f, 0.7f, wb * 0.5f); cols.Add(tub);
            }
            else
            {   // a sphere per wheel (rolls up curbs smaller than the wheel) + the frame / rider at chest height for walls
                float wr = Mathf.Max(0.22f, r.wheelR);
                var rear = go.AddComponent<SphereCollider>(); rear.radius = wr; rear.center = new Vector3(0f, wr, 0f); cols.Add(rear);
                var front = go.AddComponent<SphereCollider>(); front.radius = wr; front.center = new Vector3(0f, wr, wb); cols.Add(front);
                var frame = go.AddComponent<CapsuleCollider>(); frame.direction = 2; frame.radius = 0.28f; frame.height = wb + 0.3f; frame.center = new Vector3(0f, 1.0f, wb * 0.5f); cols.Add(frame);
            }
            foreach (var c in cols) c.material = _slick;
            r.cols.Clear(); r.cols.AddRange(cols);
            _trashIgnored.Clear(); _trashScan = 0f;
            // never collide with the rider's own capsule (he's carried along with it) or the parked ride's box
            try { foreach (var pc in me.transform.root.GetComponentsInChildren<Collider>(true)) foreach (var c in cols) Physics.IgnoreCollision(c, pc, true); } catch { }
            if (r.col != null) foreach (var c in cols) Physics.IgnoreCollision(c, r.col, true);
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = kart ? 140f : r.kind == Kind.Moped ? 120f : 85f;      // ride + rider
            rb.drag = 0f; rb.angularDrag = 0f; rb.useGravity = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;   // fast + thin floors: no tunnelling
            rb.constraints = RigidbodyConstraints.FreezeRotation;                   // upright; yaw is set by the steering
            r.body = go; r.rb = rb;
            _vSet = Vector3.zero; _safeOk = false; _safeAt = 0f; _airVy = 0f; _safeTrail.Clear();
            Trace($"rides: {sp.name} body on (Rigidbody, {cols.Count} colliders, {rb.mass:F0} kg, wheelbase {wb:F2} m)");
        }

        /// <summary>The parked ride put down on the ground under it (its own parts and the rider skipped).</summary>
        void SetDown(Ride r)
        {
            try
            {
                var at = r.go.transform.position;
                float best = float.MaxValue; Vector3 g = default; bool found = false;
                // v0.22.18 (Austin: the moped "was in the air" after he got off holding a crate: 'set down -1.28 m', i.e. lifted
                // onto the crate): the ray starts just over the wheels, skips loose / carried props and the ride's own body,
                // and never lifts the ride more than 0.4 m
                float wheelsY = at.y - r.lift;
                foreach (var h in Physics.RaycastAll(new Vector3(at.x, wheelsY + 0.5f, at.z), Vector3.down, 40f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (h.collider == null || h.collider.transform.IsChildOf(r.go.transform)) continue;
                    if (r.body != null && h.collider.transform.IsChildOf(r.body.transform)) continue;
                    var hrb = h.collider.attachedRigidbody;
                    if (hrb != null && !hrb.isKinematic) continue;
                    if (h.point.y > wheelsY + 0.4f) continue;
                    if (_trashIgnored.Contains(h.collider.Pointer)) continue;
                    try { if (h.collider.GetComponentInParent<Player>() != null) continue; } catch { }
                    if (h.distance < best) { best = h.distance; g = h.point; found = true; }
                }
                if (!found) return;
                var to = new Vector3(at.x, g.y + r.lift, at.z);
                if (Mathf.Abs(to.y - at.y) > 0.05f) Trace($"rides: {Spec[r.kind].name} set down {(at.y - to.y):F2} m onto the ground");
                r.go.transform.position = to;
            }
            catch (Exception e) { Trace($"rides: set down: {e.Message}"); }
        }

        // v0.22.18 (Austin: "i got on the moped and was able to pick up a crate"): riding, Schedule I's interaction search
        // mask is empty (its rays find nothing to hover, pick up or drag) and anything already being dragged is let go; restored on getting off
        Il2CppScheduleOne.Interaction.InteractionManager _im;
        Il2CppScheduleOne.Dragging.DragManager _dm;
        int _imMask = -1;
        void NoGrabWhileRiding(bool riding)
        {
            try
            {
                if (riding)
                {
                    if (_im == null) _im = UnityEngine.Object.FindObjectOfType<Il2CppScheduleOne.Interaction.InteractionManager>();
                    if (_dm == null) _dm = UnityEngine.Object.FindObjectOfType<Il2CppScheduleOne.Dragging.DragManager>();
                    if (_im != null && _imMask < 0) { _imMask = _im.interaction_SearchMask.value; _im.interaction_SearchMask = new LayerMask { value = 0 }; Trace($"rides: Schedule I's interaction off while riding (search mask {_imMask} -> 0)"); }
                    if (_dm != null && _dm.IsDragging) { _dm.StopDragging(Vector3.zero); Trace("rides: let go of what was being dragged"); }
                }
                else if (_imMask >= 0)
                {
                    if (_im != null) _im.interaction_SearchMask = new LayerMask { value = _imMask };
                    _imMask = -1;
                }
            }
            catch (Exception e) { _im = null; _dm = null; Trace($"rides: interaction block: {e.Message}"); }
        }

        void DropBody(Ride r)
        {
            if (r.body != null) UnityEngine.Object.Destroy(r.body);
            r.body = null; r.rb = null;
        }

        /// <summary>The nearest hit along a cast that isn't the ride itself or the rider.</summary>
        // ---- trash (v0.22.5, Austin: "the vehicles hit all the trash make the bike, moped and go kard no colission with
        // trash"): every few frames, Schedule I's trash near the ride (TrashItem / TrashBag, up the parents) is told never
        // to collide with the ride's body, and the ride's own casts (crash / bump / curb / ground) skip it too
        readonly HashSet<IntPtr> _trashIgnored = new();
        float _trashScan;

        // v0.22.11 (Austin: "theres still junk in the road that bounces the bike, moped and go kart"): trash was ignored (173
        // pieces in one ride), but boxes and other loose clutter still bounced / crashed the rides ('Box' crashed the kart
        // at 47 km/h). Junk now = trash, any loose physics prop under 60 kg, and small low clutter (< 0.7 m every way).
        // Hydrants, posts, vending machines, walls stay solid. v0.22.13 (Austin: "the go cart is pushing the trash out of the
        // way so there still collision"): the v0.22.11 knock-it-away is gone; junk doesn't react to the rides at all.
        static bool IsTrash(Collider c, float groundY)
        {
            try
            {
                if (c.GetComponentInParent<Il2CppScheduleOne.Trash.TrashItem>() != null) return true;
                if (c.GetComponentInParent<Il2CppScheduleOne.Trash.TrashBag>() != null) return true;
                var rb = c.attachedRigidbody;
                if (rb != null && !rb.isKinematic && rb.mass < 60f) return true;               // a loose physics prop
                var b = c.bounds.size;
                // v0.22.12 (Austin: "make all 3 bike no collision with any road trash or ruble"): street furniture stays solid
                // by name; junk by name passes at any size up to waist height; small clutter up to 1.5 m across / 1 m tall
                string nm = Names(c);
                if (Has(nm, SolidNames) && !nm.Contains("cardboard")) return false;     // ("car" is in "cardboard")
                var bb0 = c.bounds;
                bool upFromGround = bb0.max.y > groundY + 0.08f && bb0.min.y > groundY - 0.25f;
                if (Has(nm, JunkNames) && b.y < 1.5f && upFromGround) return true;
                if (Mathf.Max(b.x, b.z) < 1.5f && b.y < 1.0f && upFromGround && c.GetComponentInParent<NPC>() == null) return true;
            }
            catch { }
            return false;
        }

        static readonly string[] JunkNames = { "rubble", "debris", "litter", "garbage", "trash", "junk", "bottle", "can_", "cup", "paper", "bag", "box", "crate", "pallet", "tire", "tyre", "cone", "brick", "plank", "cardboard", "leaves", "leaf", "rock", "stone", "scrap", "wood" };
        static readonly string[] SolidNames = { "hydrant", "post", "pole", "sign", "bollard", "bench", "vending", "mailbox", "barrier", "fence", "wall", "light", "lamp", "tree", "planter", "building", "door", "car", "vehicle", "truck", "van", "dumpster" };

        /// <summary>The collider's own name and its parents' (up to 3), lower case: what it is.</summary>
        static string Names(Collider c)
        {
            var sb = new System.Text.StringBuilder();
            var t = c.transform;
            for (int k = 0; k < 4 && t != null; k++, t = t.parent) sb.Append(t.name.ToLowerInvariant()).Append('|');
            return sb.ToString();
        }
        static bool Has(string names, string[] keys) { foreach (var k in keys) if (names.Contains(k)) return true; return false; }

        void IgnoreTrash(Ride r)
        {
            if (r.body == null || Time.time < _trashScan) return;
            _trashScan = Time.time + 0.1f;
            int added = 0;
            // v0.22.13: ahead of the ride, as far as it can get before the next scans (at 85 km/h the kart covered ~5 m
            // between v0.22.12's 6 m scans, so junk reached the bumper before it was made pass-through)
            var v = r.rb != null ? r.rb.velocity : Vector3.zero; v.y = 0;
            var centre = r.body.transform.position + v * 0.3f;
            float radius = 6f + v.magnitude * 0.6f;
            foreach (var c in Physics.OverlapSphere(centre, radius, ~0, QueryTriggerInteraction.Ignore))
            {
                if (c == null || _trashIgnored.Contains(c.Pointer) || (r.rb != null && c.attachedRigidbody == r.rb)) continue;
                if (!IsTrash(c, r.body.transform.position.y)) continue;
                foreach (var bc in r.cols) if (bc != null) Physics.IgnoreCollision(bc, c, true);
                _trashIgnored.Add(c.Pointer); added++;
            }
            if (added > 0 && Time.time > _trashLog) { _trashLog = Time.time + 10f; Trace($"rides: passing through {_trashIgnored.Count} pieces of trash / junk so far"); }
        }
        float _trashLog;

        bool CastAway(Player me, Ride r, Vector3 from, float radius, Vector3 dir, float dist, out RaycastHit best)
        {
            best = default; bool any = false;
            foreach (var h in Physics.SphereCastAll(from, radius, dir, dist, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.collider == null) continue;
                if (_trashIgnored.Contains(h.collider.Pointer)) continue;
                if (r.rb != null && h.collider.attachedRigidbody == r.rb) continue;
                if (h.transform.IsChildOf(me.transform.root) || h.transform.IsChildOf(r.go.transform)) continue;
                if (!any || h.distance < best.distance) { best = h; any = true; }
            }
            return any;
        }

        float _liftUntil;

        // ---- v0.22.27 (Austin: "i want to be able to hit other cars and knock them around and off their path the way you
        // can in gta5"): Schedule I's cars are real rigidbodies near the player (LandVehicle.Rb; far ones go kinematic).
        // GTA's rule of thumb is mass decides who loses, but it's arcade about it: a light vehicle still shoves a car. Our
        // momentum along the hit (ride + rider mass x closing speed) goes into the car at the contact point, boosted
        // x4, so a centre hit shunts it and an off-centre hit spins it off its line; its AI re-plans its route afterwards.
        readonly Dictionary<IntPtr, float> _rammedAt = new();
        void RamCar(Ride r, Il2CppScheduleOne.Vehicles.LandVehicle car, RaycastHit hit, float now)
        {
            if (_rammedAt.TryGetValue(car.Pointer, out var at) && now - at < 0.4f) return;
            _rammedAt[car.Pointer] = now;
            Rigidbody crb = null;
            try { crb = car.GetComponent<Rigidbody>(); if (crb == null) crb = car.GetComponentInChildren<Rigidbody>(); } catch { }
            string name = "car"; try { name = car.gameObject.name; } catch { }
            if (crb == null) { Trace($"rides: hit '{name}' but found no physics body on it"); return; }
            if (crb.isKinematic) { Trace($"rides: hit '{name}' but it isn't physically simulated right now (kinematic)"); return; }
            var v = _vSet; v.y = 0;
            var into = -new Vector3(hit.normal.x, 0, hit.normal.z);
            if (into.sqrMagnitude < 0.01f) into = v.normalized; else into.Normalize();
            float closing = Mathf.Max(0f, Vector3.Dot(v - crb.velocity, into));
            if (closing < 1.5f) return;
            float mass = r.rb != null ? r.rb.mass : 120f;
            float impulse = mass * closing * 4f;
            // mostly along the hit, partly along our travel (a T-bone carries it sideways down the road), a touch of lift
            var dir = (into * 0.7f + v.normalized * 0.3f).normalized + Vector3.up * 0.08f;
            crb.AddForceAtPosition(dir * impulse, hit.point, ForceMode.Impulse);
            Sfx.Play(hit.point, Mathf.Clamp01(0.4f + closing / 12f), "CartCrash06EQSML", "CartCrash05EQS");
            float dv = impulse / Mathf.Max(1f, crb.mass);
            Trace($"rides: rammed '{name}' at {closing * 3.6f:F0} km/h: {impulse:F0} N s into its {crb.mass:F0} kg ({dv * 3.6f:F0} km/h shove)");
        }

        // ---- v0.22.24 (Austin: "give the vehicles jump phsics like in gta5 where if its got speed and goes up it jumps a
        // but off inclines"): GTA's cars keep their momentum: driving up a ramp or rise gives the body upward speed, and
        // where the road levels off or drops away that speed carries it into the air instead of being glued down. The
        // road's climb rate is remembered over the last moment (_rampVy); when the road under the wheels stops climbing
        // that fast at speed, the ride leaves the ground with the climb it had (seams / bumps are too short to count).
        float _rampVy, _groundPitch, _bodyPitch;
        bool Launch(Ride r, Vector3 hv, float follow, float now, ref float vy)
        {
            float dt = Mathf.Max(Time.deltaTime, 1e-3f);
            float speed = hv.magnitude;
            float prev = _rampVy;
            // climbs build up over ~0.15 s (a seam's one-frame kick doesn't), drops register at once
            _rampVy = follow > _rampVy ? Mathf.Lerp(_rampVy, follow, 1f - Mathf.Exp(-7f * dt)) : follow;
            if (speed < 5f || prev < 1.2f || follow > prev - 1.0f) return false;
            vy = prev * 0.95f;
            _liftUntil = now + 0.3f;
            _rampVy = 0f;
            Trace($"rides: {Spec[r.kind].name} jumped off a rise at {speed * 3.6f:F0} km/h ({vy:F1} m/s up)");
            return true;
        }

        // ---- v0.22.23 (Austin: "give the vehicles the same impact as gta5 where if it hit something it bounces off of it"):
        // GTA's cars don't stop dead on a wall: the part of the velocity going into the wall comes back out (restitution),
        // the part along it carries on with some scrape, and a glancing hit swings the nose round along the wall.
        // Head-on at speed, it rebounds a little way back; a scrape keeps most of its speed.
        float _bounceUntil, _squash;
        const float Restitution = 0.35f, Scrape = 0.85f;
        void Bounce(Ride r, Vector3 n, string what, float lost, float bail, float now)
        {
            if (n.y > 0.5f) return;                                  // a ramp / slope, not a wall
            var nh = new Vector3(n.x, 0, n.z);
            if (nh.sqrMagnitude < 0.01f) return;
            nh.Normalize();
            var v = _vSet; v.y = 0;
            float into = Vector3.Dot(v, nh);                         // < 0: moving into the surface
            if (into > -0.5f) return;
            var vt = v - nh * into;                                  // along the surface
            var vOut = vt * Scrape - nh * into * Restitution;        // bounced
            var rv = r.rb.velocity;
            r.rb.velocity = new Vector3(vOut.x, rv.y, vOut.z);
            // a glancing hit turns it along the wall (GTA's spin off a scrape); head-on it keeps facing the wall
            var fwd = Fwd(r);
            float headOn = Mathf.Abs(Vector3.Dot(fwd, nh));
            if (headOn < 0.8f && vt.sqrMagnitude > 1f)
            {
                var along = Vector3.Dot(vt, fwd) >= 0f ? vt.normalized : -vt.normalized;
                float turn = Vector3.SignedAngle(fwd, along, Vector3.up) * (1f - headOn) * 0.6f;
                r.yaw += turn; _wantYaw = r.yaw;
            }
            _vSet = new Vector3(vOut.x, 0, vOut.z);
            _bounceUntil = now + 0.25f;
            float hard = -into;
            if (hard > 2f && now - _bumpAt > 0.3f)
            {
                _bumpAt = now;
                if (r.kind != Kind.Bike) Sfx.Play(r.body.transform.position, Mathf.Clamp01(0.3f + hard / 10f), hard > 6f ? "CartCrash06EQSML" : "CartDmge03SML");
                Trace($"rides: {Spec[r.kind].name} bounced off '{what}': {hard * 3.6f:F0} km/h into it, out at {vOut.magnitude * 3.6f:F0} km/h{(headOn < 0.8f ? " (glancing, turned along it)" : "")}");
            }
        }

        // ---- v0.22.22 (Austin: "make the vehicles handle hitting an npc like gta5 i want it to rag doll them with gravity
        // and phisics"): GTA V's peds are Euphoria-driven ragdolls that take the car's momentum. Here: the space the ride
        // is about to sweep through is checked every frame (not just what the physics body touched, which missed most
        // people), and whoever's in it goes into Schedule I's ragdoll with the ride's speed and heading, pushed low at the
        // legs so they fold over the front and fly / tumble under real gravity. Their body stops colliding with the ride
        // for 1.5 s (no snagging on the ragdoll), the ride loses a little speed, and the hit hurts by speed through the
        // same path as every other blow (cops treat it as an assault; civilians get Bully's knockout, not death).
        readonly Dictionary<IntPtr, float> _knockedAt = new();
        readonly List<(Collider c, float until)> _ragIgnored = new();
        void RunOver(Player me, Ride r, float now)
        {
            for (int i = _ragIgnored.Count - 1; i >= 0; i--)
            {
                if (now < _ragIgnored[i].until) continue;
                try { var c = _ragIgnored[i].c; if (c != null) foreach (var bc in r.cols) if (bc != null) Physics.IgnoreCollision(bc, c, false); } catch { }
                _ragIgnored.RemoveAt(i);
            }
            if (r.rb == null || r.body == null) return;
            var v = r.rb.velocity; v.y = 0;
            float speed = v.magnitude;
            if (speed < 2.5f) return;
            float wb = r.wheelbase > 0.3f ? r.wheelbase : Spec[r.kind].wheelbase;
            var fwd = Fwd(r);
            float reach = 0.4f + speed * 0.05f;
            var centre = r.body.transform.position + fwd * ((wb + reach) * 0.5f + 0.15f) + Vector3.up * 0.9f;
            var half = new Vector3(r.kind == Kind.Kart ? 0.65f : 0.45f, 0.85f, (wb + reach) * 0.5f + 0.3f);
            foreach (var c in Physics.OverlapBox(centre, half, Quaternion.Euler(0, r.yaw, 0), ~0, QueryTriggerInteraction.Ignore))
            {
                if (c == null) continue;
                NPC npc = null;
                try { if (c.GetComponentInParent<Player>() != null) continue; npc = c.GetComponentInParent<NPC>(); } catch { }
                if (npc == null) continue;
                Knock(me, r, npc, v, now);
            }
        }

        void Knock(Player me, Ride r, NPC npc, Vector3 v, float now)
        {
            if (_knockedAt.TryGetValue(npc.Pointer, out var at) && now - at < 2f) return;
            _knockedAt[npc.Pointer] = now;
            v.y = 0;
            float speed = v.magnitude;
            var dir = speed > 0.1f ? v / speed : Fwd(r);
            var p = npc.transform.position;
            // the legs are taken out: the push lands at the shins, with lift that grows with speed (over the front, then flying)
            // v0.22.23 (Austin: "hitting someone sends them into the air over the vehicle and pushes the vehicle down"): GTA's
            // pedestrian is scooped at the legs, so the body rotates up onto / over the front while the vehicle runs on
            // underneath: most of the push is UP, and less forward than the ride is going (it passes under them)
            float k = Mathf.InverseLerp(4f, 20f, speed);
            var push = dir * Mathf.Lerp(0.8f, 0.45f, k) + Vector3.up * Mathf.Lerp(0.5f, 1.2f, k);
            float force = Mathf.Clamp(speed * 2.4f, 8f, 55f) * (r.kind == Kind.Bike ? 0.7f : 1f);
            try
            {
                foreach (var c in npc.GetComponentsInChildren<Collider>(true))
                {
                    if (c == null) continue;
                    foreach (var bc in r.cols) if (bc != null) Physics.IgnoreCollision(bc, c, true);
                    _ragIgnored.Add((c, now + 1.5f));
                }
            }
            catch { }
            float share = Mathf.Clamp01((speed - 3f) / 20f) * 0.55f;
            if (share > 0.01f) Kit.RideHit(npc, me, p + Vector3.up * 0.9f, dir, force, share);
            try { npc.Movement.ActivateRagdoll(p + Vector3.up * 0.45f + dir * -0.1f, push.normalized, force); } catch (Exception e) { Trace($"rides: ragdoll: {e.Message}"); }
            // ...and the ride takes their weight: the front sinks and the nose dips, then springs back
            _squash = Mathf.Max(_squash, Mathf.Clamp(speed * 0.012f, 0.04f, 0.16f));
            _visPitch += Mathf.Clamp(speed * 0.6f, 3f, 10f);
            // the ride pays a little for it (the bike more)
            if (r.rb != null)
            {
                float keep = r.kind == Kind.Bike ? 0.8f : r.kind == Kind.Moped ? 0.88f : 0.92f;
                var rv = r.rb.velocity;
                r.rb.velocity = new Vector3(rv.x * keep, rv.y, rv.z * keep);
                _vSet *= keep;
            }
            Sfx.Play(p + Vector3.up * 0.8f, Mathf.Clamp01(0.4f + speed / 20f), "CartDmge03SML", "CartCrash06EQSML");
            string id = "?"; try { id = npc.ID; } catch { }
            Trace($"rides: ran into {id} at {speed * 3.6f:F0} km/h: ragdolled (force {force:F0}, lift {push.y:F2}), damage {share * 100f:F0}% of their health");
        }

        // ---- v0.22.19 (Austin: "see whats causing the cart to jump if you have to put an invisible flat surface on every
        // road then do it"): the kart's rails were capsules sitting ON the road, and a capsule's round nose rides up any lip
        // under its 0.2 m radius: every road-mesh seam / sidewalk edge kicked it into the air. Like GTA's cars, it now
        // floats on four probes (its wheel corners): their road heights are averaged into one flat plane under the kart, the
        // body is held on that plane, and it follows it up steps of up to 0.3 m (curbs) instead of bouncing off them.
        float _kartJumpLog;
        bool KartSuspension(Player me, Ride r, Vector3 hv, ref float vy, float now)
        {
            var b = r.body.transform;
            float wb = r.wheelbase > 0.3f ? r.wheelbase : Spec[r.kind].wheelbase;
            var fwd = Fwd(r); var side = Quaternion.Euler(0, r.yaw, 0) * Vector3.right;
            // look a little ahead at speed so a step is met before the body reaches it
            float ahead = Mathf.Clamp(Vector3.Dot(hv, fwd) * 0.06f, 0f, 0.6f);
            float sumY = 0f; int n = 0; Vector3 nrm = Vector3.zero; float hiY = float.MinValue; string hiName = "";
            float rearY = 0f, frontY = 0f; int rearN = 0, frontN = 0;
            foreach (float z in new[] { 0f, wb + ahead })
                foreach (float x in new[] { -0.45f, 0.45f })
                {
                    var top = b.position + fwd * z + side * x + Vector3.up * 0.5f;
                    if (!CastAway(me, r, top, 0.08f, Vector3.down, 1.1f, out var h)) continue;
                    if (h.normal.y < 0.55f) continue;                                   // a wall / curb face
                    if (h.point.y > b.position.y + 0.32f) continue;                     // too tall to climb: the rails meet it
                    sumY += h.point.y; nrm += h.normal; n++;
                    if (z == 0f) { rearY += h.point.y; rearN++; } else { frontY += h.point.y; frontN++; }
                    if (h.point.y > hiY) { hiY = h.point.y; hiName = h.collider != null ? h.collider.name : "?"; }
                }
            if (n < 2) return false;                                                   // over a drop: fly
            float roadY = sumY / n;
            var gn = nrm.normalized;
            float gap = b.position.y - roadY;
            if (gap > 0.35f) return false;                                             // the road fell away (crest / ramp): fly
            float physVy = vy;
            float follow = -(gn.x * hv.x + gn.z * hv.z) / Mathf.Max(0.4f, gn.y);
            // v0.22.24 (Austin: "the go kart is loosing spead up hills and sometimes colides with them"): the body never
            // tilted (rotation frozen level), so on a hill the front of its rails dug into the slope and scrubbed speed or
            // hit it. The body now pitches with the road under its wheels (FixedStep), so its colliders lie along the hill.
            if (rearN > 0 && frontN > 0) _groundPitch = Mathf.Clamp(-Mathf.Atan2(frontY / frontN - rearY / rearN, wb + ahead) * Mathf.Rad2Deg, -35f, 35f);
            if (Launch(r, hv, follow, now, ref vy)) return false;                     // off the top of a rise: airborne
            vy = follow + Mathf.Clamp(-gap * 12f, -4f, 3.5f);                          // held on the plane, eased onto a step
            if (physVy > follow + 1.2f && now > _kartJumpLog)
            {   // something still threw it up: say what
                _kartJumpLog = now + 1f;
                Trace($"rides: kart kicked up {physVy:F1} m/s on the ground (road plane {roadY:F2}, highest '{hiName}' {hiY:F2}, gap {gap:F2}): held down");
            }
            return true;
        }

        // ---- the road sim: on getting in, the kart's four wheel lines swept 60 m ahead every 0.2 m; every jump in road
        // height between samples (a seam, a lip, a curb edge) is logged with the collider it's on
        void SimRoad(Player me, Ride r)
        {
            try
            {
                var b = r.go.transform;
                var o = b.position - Vector3.up * r.lift; var fwd = Fwd(r); var side = Quaternion.Euler(0, r.yaw, 0) * Vector3.right;
                int steps = 0, gaps = 0; var lines = new List<string>();
                foreach (float x in new[] { -0.45f, 0.45f })
                {
                    float last = float.NaN;
                    for (float d = 0f; d <= 60f; d += 0.2f)
                    {
                        var top = o + fwd * d + side * x + Vector3.up * 2f;
                        float best = float.MaxValue; RaycastHit bh = default;
                        foreach (var h in Physics.RaycastAll(top, Vector3.down, 6f, ~0, QueryTriggerInteraction.Ignore))
                        {
                            if (h.collider == null || h.collider.transform.IsChildOf(b)) continue;
                            if (h.collider.attachedRigidbody != null && !h.collider.attachedRigidbody.isKinematic) continue;
                            try { if (h.collider.GetComponentInParent<Player>() != null || h.collider.GetComponentInParent<NPC>() != null) continue; } catch { }
                            if (h.distance < best) { best = h.distance; bh = h; }
                        }
                        if (best == float.MaxValue) { gaps++; last = float.NaN; continue; }
                        float y = bh.point.y;
                        if (!float.IsNaN(last) && Mathf.Abs(y - last) > 0.03f)
                        {
                            steps++;
                            if (lines.Count < 25) lines.Add($"{(x < 0 ? "L" : "R")} {d,5:F1} m: {(y - last) * 100f,6:F1} cm on '{bh.collider.name}' (normal y {bh.normal.y:F2}){(IsTrash(bh.collider, last) ? " [junk, passed through]" : "")}");
                        }
                        last = y;
                    }
                }
                Trace($"rides: road sim, kart wheel lines 60 m ahead: {steps} height steps over 3 cm, {gaps} samples with no ground");
                foreach (var l in lines) Trace("rides: road sim   " + l);
            }
            catch (Exception e) { Trace($"rides: road sim: {e.Message}"); }
        }

        /// <summary>The road under the wheels: its normal (averaged) and how far the body's origin sits above it.</summary>
        bool GroundUnder(Player me, Ride r, out Vector3 normal, out float gap)
        {
            normal = Vector3.up; gap = 0f;
            var b = r.body.transform; var up = Vector3.up;
            float wr = r.kind == Kind.Kart ? 0.2f : Mathf.Max(0.22f, r.wheelR);
            float wb = r.wheelbase > 0.3f ? r.wheelbase : Spec[r.kind].wheelbase;
            int n = 0; Vector3 sum = Vector3.zero; float rear = float.NaN;
            foreach (float z in new[] { 0f, wb })
                if (CastAway(me, r, b.position + b.forward * z + up * (wr + 0.1f), 0.12f, Vector3.down, wr + 0.35f, out var h))
                {
                    if (h.normal.y < 0.5f) continue;                       // a wall / curb face, not road
                    sum += h.normal; n++;
                    if (z == 0f) rear = h.point.y;
                }
            if (n == 0) return false;
            normal = sum.normalized;
            gap = float.IsNaN(rear) ? 0f : b.position.y - rear;
            return true;
        }

        bool Grounded(Player me, Ride r)
        {
            var b = r.body.transform; var up = Vector3.up;
            float wr = r.kind == Kind.Kart ? 0.2f : Mathf.Max(0.22f, r.wheelR);
            float wb = r.wheelbase > 0.3f ? r.wheelbase : Spec[r.kind].wheelbase;
            foreach (float z in new[] { 0f, wb })
                if (CastAway(me, r, b.position + b.forward * z + up * (wr + 0.1f), 0.12f, Vector3.down, wr + 0.25f, out _)) return true;
            return false;
        }

        /// <summary>A curb / step low in front of the front wheel with clear space above it: give the body a lift.</summary>
        bool CurbAhead(Player me, Ride r, Vector3 fwd)
        {
            var b = r.body.transform;
            float wb = r.wheelbase > 0.3f ? r.wheelbase : Spec[r.kind].wheelbase;
            var nose = b.position + fwd * (wb + (r.kind == Kind.Kart ? 0.3f : Mathf.Max(0.22f, r.wheelR)));
            if (!CastAway(me, r, nose + Vector3.up * 0.08f - fwd * 0.1f, 0.06f, fwd, 0.35f, out var low)) return false;
            if (low.normal.y > 0.6f) return false;                                   // a slope, not a step
            return !CastAway(me, r, nose + Vector3.up * 0.42f - fwd * 0.1f, 0.06f, fwd, 0.45f, out _);   // only low ones
        }

        /// <summary>Physics took `lost` m/s off us: knock a pedestrian over, crash into something hard, or just bump.</summary>
        bool Hit(Player me, Ride r, float lost, float now)
        {
            var sp = Spec[r.kind];
            var dir = _vSet.normalized;
            float wb = r.wheelbase > 0.3f ? r.wheelbase : sp.wheelbase;
            var from = r.body.transform.position + Vector3.up * (r.kind == Kind.Kart ? 0.4f : 0.7f) + dir * (wb * 0.5f);
            string what = "something"; bool small = false; Vector3 n = -dir; NPC npc = null;
            if (CastAway(me, r, from, 0.4f, dir, wb + 1.2f, out var hit))
            {
                what = hit.transform.name; n = hit.normal;
                var bsz = hit.collider.bounds.size;
                small = bsz.y < 1.3f && Mathf.Max(bsz.x, bsz.z) < 1.0f;               // hydrants, post boxes, bins, cones
                try { npc = hit.collider.GetComponentInParent<NPC>(); } catch { }
            }
            // v0.22.12 (Austin: yes to the kart hopping curbs): a low curb / kerb (hit under ~35 cm over the ride's ground) is
            // climbed: the speed it took is given back with a lift, instead of a bump or a crash (the kart lost 14-30 km/h at
            // every 'Sidewalk', and crashed on one at 83 km/h)
            if (hit.collider != null)
            {
                string cn = Names(hit.collider);
                bool kerb = cn.Contains("sidewalk") || cn.Contains("curb") || cn.Contains("kerb") || cn.Contains("pavement");
                if (kerb && hit.point.y < r.body.transform.position.y + 0.35f && Mathf.Abs(hit.normal.y) < 0.6f)
                {
                    var rv = r.rb.velocity;
                    r.rb.velocity = new Vector3(_vSet.x, Mathf.Max(rv.y, r.kind == Kind.Kart ? 3.6f : 2.8f), _vSet.z);
                    _liftUntil = now + 0.3f;
                    if (now - _bumpAt > 0.6f) { _bumpAt = now; Trace($"rides: hopped the curb '{hit.collider.name}' at {_vSet.magnitude * 3.6f:F0} km/h"); }
                    return false;
                }
            }
            if (hit.collider != null && hit.normal.y > 0.5f) return false;            // v0.22.24: a hill / ramp ahead: climbed, not hit
            if (npc != null)
            {   // GTA: whoever's in the way goes flying; it costs you some speed, not the ride
                Knock(me, r, npc, _vSet, now);
                return false;
            }
            if (hit.collider != null)
            {   // v0.22.27: a Schedule I car takes the hit (then the ride bounces / bails as for anything solid)
                Il2CppScheduleOne.Vehicles.LandVehicle car = null;
                try { car = hit.collider.GetComponentInParent<Il2CppScheduleOne.Vehicles.LandVehicle>(); } catch { }
                if (car != null) RamCar(r, car, hit, now);
            }
            float bail = (r.kind == Kind.Kart ? 10f : r.kind == Kind.Bike ? 7.5f : 8.5f) * (small ? 1.4f : 1f);   // ~27 km/h straight in on the bike
            // v0.22.17 (Austin: "remove the eject from the go cart keep it for the buke and moped"): the kart never throws
            // him. v0.22.23: the kart always bounces; the bike / moped bounce below their bail speed
            if (r.kind == Kind.Kart || lost < bail)
            {
                Bounce(r, n, what, lost, bail, now);
                return false;
            }
            if (lost >= bail)
            {
                Trace($"rides: crashed the {sp.name}: lost {lost * 3.6f:F0} km/h into '{what}' (was {_vSet.magnitude * 3.6f:F0} km/h)");
                Voice.Say("Jimmy", "PBIKEC");
                if (r.kind == Kind.Moped) Sfx.Play(r.body.transform.position, 1f, "CartCrash05EQS");
                Crash(r, n, r.body.transform.position + Fwd(r) * r.rearBack);
                return true;
            }
            if (lost > 2.5f && now - _bumpAt > 0.6f)
            {
                _bumpAt = now; Trace($"rides: bumped '{what}', lost {lost * 3.6f:F0} km/h");
                if (r.kind != Kind.Bike) Sfx.Play(r.body.transform.position, Mathf.Clamp01(lost / 8f), "CartDmge03SML");
            }
            return false;
        }
    }
}
