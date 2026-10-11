"""Cadence: a self-hosted song request and playlist companion for Lidarr Songs."""
import concurrent.futures
import hashlib
import hmac
import http.cookies
import json
import mimetypes
import os
from pathlib import Path
import secrets
import sqlite3
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

DATA = Path(os.getenv('CADENCE_DATA', '/data'))
STATIC = Path(__file__).parent / 'static'
MUSIC = Path(os.getenv('CADENCE_MUSIC_ROOT', '/music')).resolve()
URL = os.getenv('CADENCE_LIDARR_URL', 'http://lidarr-tracks:8686').rstrip('/')
KEY = os.getenv('CADENCE_LIDARR_API_KEY', '')
SECURE = os.getenv('CADENCE_SECURE_COOKIE', 'false').lower() == 'true'
CACHE = {'at': 0, 'tracks': [], 'artists': []}
LOCK = threading.Lock()
FAILURES = {}

class Problem(Exception):
    def __init__(self, message, status=400):
        super().__init__(message)
        self.status = status

def connect():
    db = sqlite3.connect(DATA / 'cadence.sqlite', timeout=20)
    db.row_factory = sqlite3.Row
    db.execute('PRAGMA foreign_keys=ON')
    return db

def password_hash(password, salt=None):
    if len(password) < 10 or len(password) > 256:
        raise Problem('Use a password between 10 and 256 characters.')
    salt = salt or secrets.token_hex(16)
    digest = hashlib.scrypt(password.encode(), salt=salt.encode(), n=16384, r=8, p=1).hex()
    return salt + ':' + digest

def initialize():
    DATA.mkdir(parents=True, exist_ok=True)
    with connect() as db:
        db.executescript('''
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS users(id INTEGER PRIMARY KEY, name TEXT UNIQUE COLLATE NOCASE, password TEXT, role TEXT);
        CREATE TABLE IF NOT EXISTS sessions(token TEXT PRIMARY KEY, user_id INTEGER REFERENCES users(id), csrf TEXT, expires REAL);
        CREATE TABLE IF NOT EXISTS requests(id INTEGER PRIMARY KEY, user_id INTEGER REFERENCES users(id), identity TEXT, track TEXT, status TEXT DEFAULT 'pending', note TEXT DEFAULT '', created REAL);
        CREATE UNIQUE INDEX IF NOT EXISTS active_request ON requests(identity) WHERE status != 'declined';
        CREATE TABLE IF NOT EXISTS playlists(id INTEGER PRIMARY KEY, user_id INTEGER REFERENCES users(id), name TEXT, description TEXT DEFAULT '', created REAL);
        CREATE TABLE IF NOT EXISTS entries(playlist_id INTEGER REFERENCES playlists(id) ON DELETE CASCADE, identity TEXT, track TEXT, position INTEGER, PRIMARY KEY(playlist_id,identity));
        ''')
        if not db.execute('SELECT 1 FROM users').fetchone():
            password = os.getenv('CADENCE_ADMIN_PASSWORD', '')
            if not password:
                raise RuntimeError('Set CADENCE_ADMIN_PASSWORD before the first startup (at least 10 characters).')
            name = os.getenv('CADENCE_ADMIN_USER', 'admin')
            db.execute('INSERT INTO users(name,password,role) VALUES(?,?,?)', (name, password_hash(password), 'admin'))
        db.execute('DELETE FROM sessions WHERE expires < ?', (time.time(),))

def upstream(path, method='GET', payload=None, raw=False):
    if not KEY:
        raise Problem('Set CADENCE_LIDARR_API_KEY in your .env, then recreate the Cadence container.', 503)
    request = urllib.request.Request(URL + (path if raw else '/api/v1/' + path),
        data=json.dumps(payload).encode() if payload is not None else None,
        headers={'X-Api-Key': KEY, 'Content-Type': 'application/json'}, method=method)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            body = response.read()
            return (body, response.headers.get('Content-Type', 'image/jpeg')) if raw else (json.loads(body) if body else {})
    except urllib.error.HTTPError as error:
        body = error.read(8192).decode(errors='replace')
        try:
            value = json.loads(body)
            body = value.get('message', body) if isinstance(value, dict) else str(value)
        except ValueError:
            pass
        raise Problem('Lidarr: ' + body[:600], 502) from None
    except (OSError, ValueError):
        raise Problem('Cannot reach Lidarr. Check its URL, API key and container network.', 502) from None

def identity(track):
    return track.get('foreignTrackId') or 'local:' + str(track['id'])

def catalog(force=False):
    with LOCK:
        if not force and CACHE['at'] > time.time() - 45:
            return CACHE
        artists = upstream('artist')
        albums = {a['id']: a for a in upstream('album')}
        def get_tracks(artist):
            result = []
            for track in upstream('track?artistId=' + str(artist['id'])):
                album = albums.get(track['albumId'], {})
                result.append({k: track.get(k) for k in ('id', 'foreignTrackId', 'foreignRecordingId', 'title', 'duration', 'albumId', 'artistId', 'hasFile', 'trackFileId')} | {
                    'artist': artist['artistName'], 'album': album.get('title', 'Unknown album'),
                    'year': (album.get('releaseDate') or '')[:4], 'genres': artist.get('genres', []),
                    'identity': identity(track), 'cover': '/api/cover/' + str(track['albumId'])})
            return result
        with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
            tracks = [t for group in pool.map(get_tracks, artists) for t in group]
        CACHE.update(at=time.time(), tracks=tracks, artists=artists)
        return CACHE

def find_track(track_id):
    found = next((t for t in catalog()['tracks'] if t['id'] == int(track_id)), None)
    if not found:
        raise Problem('Track is no longer in the catalog. Refresh the library.', 404)
    return found

def resolve_snapshot(snapshot):
    saved = json.loads(snapshot)
    return next((t for t in catalog()['tracks'] if t['identity'] == saved['identity']), saved)

def stream_path(track_id):
    track = find_track(track_id)
    if not track['hasFile']:
        raise Problem('This song has not been downloaded.', 404)
    resource = upstream('trackfile/' + str(track['trackFileId']))
    path = Path(resource['path']).resolve()
    if not path.is_relative_to(MUSIC) or not path.is_file():
        raise Problem('Audio is unavailable. Mount the music folder at the same /music path used by Lidarr.', 404)
    return path

def byte_range(header, size):
    if not header:
        return 0, size - 1, False
    try:
        unit, value = header.split('=', 1)
        first, last = value.split('-', 1)
        if unit != 'bytes' or ',' in value:
            raise ValueError()
        if first:
            start = int(first)
            end = min(int(last), size - 1) if last else size - 1
        else:
            suffix = int(last)
            if suffix <= 0:
                raise ValueError()
            start, end = max(0, size - suffix), size - 1
        if start < 0 or start >= size or end < start:
            raise ValueError()
        return start, end, True
    except ValueError:
        raise Problem('Invalid audio byte range.', 416)

class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        # Avoid writing session tokens or request bodies to logs.
        print('%s %s' % (self.command, urllib.parse.urlsplit(self.path).path), flush=True)

    def reply(self, data, status=200, headers=None):
        body = json.dumps(data).encode()
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Cache-Control', 'no-store')
        self.send_header('Content-Length', str(len(body)))
        for key, value in (headers or {}).items():
            self.send_header(key, value)
        self.end_headers()
        self.wfile.write(body)

    def user(self, mutation=False):
        cookies = http.cookies.SimpleCookie()
        try:
            cookies.load(self.headers.get('Cookie', ''))
        except http.cookies.CookieError:
            raise Problem('Sign in to continue.', 401)
        token = cookies.get('cadence_session')
        with connect() as db:
            user = db.execute('SELECT users.*, sessions.csrf FROM sessions JOIN users ON users.id=sessions.user_id WHERE token=? AND expires>?',
                (token.value if token else '', time.time())).fetchone()
        if not user:
            raise Problem('Sign in to continue.', 401)
        if mutation and not hmac.compare_digest(self.headers.get('X-CSRF-Token', ''), user['csrf']):
            raise Problem('Refresh the page and try again.', 403)
        return dict(user)

    def admin(self, user):
        if user['role'] != 'admin':
            raise Problem('Administrator access required.', 403)

    def body(self):
        length = int(self.headers.get('Content-Length', '0'))
        if length > 131072:
            raise Problem('Request is too large.', 413)
        try:
            return json.loads(self.rfile.read(length) or b'{}')
        except ValueError:
            raise Problem('Invalid JSON.') from None

    def do_GET(self):
        self.handle_call(False)

    def do_POST(self):
        self.handle_call(True)

    def handle_call(self, mutation):
        try:
            parsed = urllib.parse.urlsplit(self.path)
            path = parsed.path
            params = urllib.parse.parse_qs(parsed.query)
            if path == '/api/health' and not mutation:
                return self.reply({'status': 'ok'})
            if path == '/api/login' and mutation:
                return self.login(self.body())
            if not path.startswith('/api/'):
                if mutation:
                    raise Problem('Not found.', 404)
                return self.static(path)
            user = self.user(mutation)
            payload = self.body() if mutation else {}
            if path == '/api/me' and not mutation:
                return self.reply({k: user[k] for k in ('id', 'name', 'role', 'csrf')})
            if path == '/api/logout' and mutation:
                cookie = http.cookies.SimpleCookie(self.headers.get('Cookie', ''))
                with connect() as db:
                    db.execute('DELETE FROM sessions WHERE token=?', (cookie['cadence_session'].value,))
                return self.reply({}, headers={'Set-Cookie': 'cadence_session=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0'})
            if path == '/api/catalog' and not mutation:
                data = catalog(params.get('refresh') == ['1'])
                return self.reply({'tracks': data['tracks'], 'artists': [{'id': a['id'], 'name': a['artistName']} for a in data['artists']]})
            if path.startswith('/api/cover/') and not mutation:
                album_id = int(path.split('/')[-1])
                body, content_type = upstream('/MediaCover/Albums/' + str(album_id) + '/cover.jpg', raw=True)
                self.send_response(200)
                self.send_header('Content-Type', content_type)
                self.send_header('Cache-Control', 'private, max-age=3600')
                self.send_header('Content-Length', str(len(body)))
                self.end_headers()
                return self.wfile.write(body)
            if path.startswith('/api/stream/') and not mutation:
                return self.stream(stream_path(int(path.split('/')[-1])))
            if path == '/api/requests':
                return self.requests(user, payload, mutation)
            if path == '/api/playlists':
                return self.playlists(user, payload, mutation)
            if path == '/api/users':
                self.admin(user)
                with connect() as db:
                    if mutation:
                        name = str(payload.get('name', '')).strip()[:60]
                        if not name:
                            raise Problem('A username is required.')
                        db.execute('INSERT INTO users(name,password,role) VALUES(?,?,?)', (name, password_hash(str(payload.get('password', ''))), 'user'))
                    return self.reply([dict(r) for r in db.execute('SELECT id,name,role FROM users')])
            if path == '/api/sources':
                self.admin(user)
                track = find_track(payload['trackId']) if mutation else find_track(params.get('trackId', ['0'])[0])
                if not mutation:
                    return self.reply(upstream('trackdownload/search?' + urllib.parse.urlencode({'trackId': track['id'], 'albumFallback': 'true'})))
                result = upstream('trackdownload/grab', 'POST', {'trackId': track['id'], 'candidateId': payload['candidateId']})
                with connect() as db:
                    db.execute("UPDATE requests SET status='downloading',note='' WHERE identity=? AND status!='declined'", (track['identity'],))
                return self.reply(result)
            if path == '/api/activity' and not mutation:
                self.admin(user)
                return self.reply({'queue': upstream('queue?pageSize=100'), 'soulseek': upstream('trackdownload/soulseek')})
            if path == '/api/artists':
                self.admin(user)
                if not mutation:
                    return self.reply(upstream('artist/lookup?' + urllib.parse.urlencode({'term': params.get('q', [''])[0]})))
                artist = next((a for a in upstream('artist/lookup?term=lidarr:' + urllib.parse.quote(str(payload['foreignArtistId']))) if a['foreignArtistId'] == payload['foreignArtistId']), None)
                roots, quality, metadata = upstream('rootfolder'), upstream('qualityprofile'), upstream('metadataprofile')
                if not artist or not roots or not quality or not metadata:
                    raise Problem('Configure a root folder, quality profile and metadata profile in Lidarr first.')
                artist.update(rootFolderPath=roots[0]['path'], qualityProfileId=quality[0]['id'], metadataProfileId=metadata[0]['id'], monitored=False,
                    monitorNewItems='none', addOptions={'monitor': 'none', 'searchForMissingAlbums': False})
                result = upstream('artist', 'POST', artist)
                CACHE['at'] = 0
                return self.reply({'name': result.get('artistName'), 'message': 'Artist added. Lidarr is loading metadata; refresh the catalog shortly.'})
            raise Problem('Not found.', 404)
        except Problem as error:
            self.reply({'error': str(error)}, error.status)
        except (KeyError, ValueError, TypeError):
            self.reply({'error': 'Invalid request fields.'}, 400)
        except sqlite3.IntegrityError:
            self.reply({'error': 'This name or active request already exists.'}, 409)
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception as error:
            print('Request failed:', type(error).__name__, flush=True)
            self.reply({'error': 'Unexpected server error. Check the container logs.'}, 500)

    def login(self, payload):
        origin = self.headers.get('Origin')
        if origin and urllib.parse.urlsplit(origin).netloc != self.headers.get('Host'):
            raise Problem('Cross-site sign-in is not allowed.', 403)
        key = self.client_address[0]
        now = time.time()
        with LOCK:
            attempts = [t for t in FAILURES.get(key, []) if t > now - 60]
            if len(attempts) >= 8:
                raise Problem('Too many sign-in attempts. Try again in one minute.', 429)
            FAILURES[key] = attempts + [now]
        with connect() as db:
            user = db.execute('SELECT * FROM users WHERE name=?', (str(payload.get('name', ''))[:60],)).fetchone()
            saved = user['password'] if user else password_hash('dummy-password')
            try:
                valid = hmac.compare_digest(saved, password_hash(str(payload.get('password', '')), saved.split(':')[0]))
            except Problem:
                valid = False
            if not user or not valid:
                raise Problem('Incorrect username or password.', 401)
            token, csrf = secrets.token_urlsafe(32), secrets.token_urlsafe(32)
            db.execute('INSERT INTO sessions VALUES(?,?,?,?)', (token, user['id'], csrf, now + 604800))
        self.reply({'name': user['name'], 'id': user['id'], 'role': user['role'], 'csrf': csrf}, headers={'Set-Cookie':
            'cadence_session=' + token + '; Path=/; HttpOnly; SameSite=Strict; Max-Age=604800' + ('; Secure' if SECURE else '')})

    def requests(self, user, payload, mutation):
        with connect() as db:
            if mutation:
                action = payload.get('action', 'create')
                if action == 'create':
                    track = find_track(payload['trackId'])
                    if track['hasFile']:
                        raise Problem('This song is already available.')
                    db.execute('INSERT INTO requests(user_id,identity,track,created) VALUES(?,?,?,?)', (user['id'], track['identity'], json.dumps(track), time.time()))
                else:
                    self.admin(user)
                    status = {'approve': 'approved', 'decline': 'declined'}.get(action)
                    if not status:
                        raise Problem('Unknown request action.')
                    db.execute('UPDATE requests SET status=?,note=? WHERE id=?', (status, str(payload.get('note', ''))[:500], int(payload['id'])))
            rows = db.execute('SELECT requests.*,users.name FROM requests JOIN users ON users.id=requests.user_id ORDER BY created DESC').fetchall()
            result = []
            for row in rows:
                if user['role'] != 'admin' and row['user_id'] != user['id']:
                    continue
                track = resolve_snapshot(row['track'])
                status = 'available' if track.get('hasFile') and row['status'] != 'declined' else row['status']
                result.append({'id': row['id'], 'track': track, 'status': status, 'note': row['note'], 'user': row['name'], 'created': row['created']})
            return self.reply(result)

    def playlists(self, user, payload, mutation):
        with connect() as db:
            if mutation:
                action = payload.get('action', 'create')
                if action == 'create':
                    name = str(payload.get('name', '')).strip()[:100]
                    if not name:
                        raise Problem('Give your playlist a name.')
                    db.execute('INSERT INTO playlists(user_id,name,description,created) VALUES(?,?,?,?)', (user['id'], name, str(payload.get('description', ''))[:500], time.time()))
                else:
                    playlist = db.execute('SELECT * FROM playlists WHERE id=? AND user_id=?', (int(payload['id']), user['id'])).fetchone()
                    if not playlist:
                        raise Problem('Playlist not found.', 404)
                    if action == 'delete':
                        db.execute('DELETE FROM playlists WHERE id=?', (playlist['id'],))
                    elif action == 'rename':
                        name = str(payload.get('name', '')).strip()[:100]
                        if not name:
                            raise Problem('Give your playlist a name.')
                        db.execute('UPDATE playlists SET name=? WHERE id=?', (name, playlist['id']))
                    elif action == 'add':
                        track = find_track(payload['trackId'])
                        position = db.execute('SELECT COALESCE(MAX(position),-1)+1 FROM entries WHERE playlist_id=?', (playlist['id'],)).fetchone()[0]
                        db.execute('INSERT OR IGNORE INTO entries VALUES(?,?,?,?)', (playlist['id'], track['identity'], json.dumps(track), position))
                    elif action == 'remove':
                        db.execute('DELETE FROM entries WHERE playlist_id=? AND identity=?', (playlist['id'], payload['identity']))
                    elif action == 'reorder':
                        keys = payload['identities']
                        existing = {r[0] for r in db.execute('SELECT identity FROM entries WHERE playlist_id=?', (playlist['id'],))}
                        if len(keys) != len(existing) or set(keys) != existing:
                            raise Problem('Playlist changed. Refresh before reordering.')
                        for position, key in enumerate(keys):
                            db.execute('UPDATE entries SET position=? WHERE playlist_id=? AND identity=?', (position, playlist['id'], key))
                    else:
                        raise Problem('Unknown playlist action.')
            result = []
            for row in db.execute('SELECT * FROM playlists WHERE user_id=? ORDER BY created DESC', (user['id'],)):
                result.append(dict(row) | {'tracks': [resolve_snapshot(e['track']) for e in db.execute('SELECT track FROM entries WHERE playlist_id=? ORDER BY position', (row['id'],))]})
            return self.reply(result)

    def stream(self, path):
        size = path.stat().st_size
        try:
            start, end, partial = byte_range(self.headers.get('Range'), size)
        except Problem:
            self.send_response(416)
            self.send_header('Content-Range', 'bytes */' + str(size))
            self.send_header('Content-Length', '0')
            self.end_headers()
            return
        self.send_response(206 if partial else 200)
        self.send_header('Content-Type', mimetypes.guess_type(path.name)[0] or 'application/octet-stream')
        self.send_header('Accept-Ranges', 'bytes')
        self.send_header('Cache-Control', 'private, no-store')
        self.send_header('Content-Length', str(end - start + 1))
        if partial:
            self.send_header('Content-Range', f'bytes {start}-{end}/{size}')
        self.end_headers()
        with path.open('rb') as audio:
            audio.seek(start)
            remaining = end - start + 1
            while remaining > 0:
                chunk = audio.read(min(65536, remaining))
                if not chunk:
                    break
                self.wfile.write(chunk)
                remaining -= len(chunk)

    def static(self, path):
        file = STATIC / {'/': 'index.html', '/app.js': 'app.js', '/styles.css': 'styles.css'}.get(path, '')
        if not file.is_file():
            raise Problem('Not found.', 404)
        body = file.read_bytes()
        self.send_response(200)
        self.send_header('Content-Type', mimetypes.guess_type(file.name)[0] or 'text/plain')
        self.send_header('Content-Length', str(len(body)))
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.send_header('X-Frame-Options', 'DENY')
        self.send_header('Content-Security-Policy', "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'; media-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")
        self.end_headers()
        self.wfile.write(body)

if __name__ == '__main__':
    initialize()
    print('Cadence listening on port 8787', flush=True)
    ThreadingHTTPServer(('0.0.0.0', 8787), Handler).serve_forever()
