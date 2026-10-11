import concurrent.futures
import http.cookiejar
import json
import os
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch
import urllib.error
import urllib.request
import server

class IntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        server.DATA = Path(cls.temp.name)
        server.MUSIC = Path(cls.temp.name) / 'music'
        server.MUSIC.mkdir()
        cls.audio = server.MUSIC / 'song.mp3'
        cls.audio.write_bytes(b'0123456789')
        cls.track = {'id': 11, 'title': 'A Song', 'foreignTrackId': 'mb-song', 'artistId': 1, 'albumId': 2, 'duration': 120000, 'hasFile': False, 'trackFileId': 7}
        def lidarr(path, method='GET', payload=None, raw=False):
            if path == 'artist': return [{'id': 1, 'artistName': 'An Artist'}]
            if path == 'album': return [{'id': 2, 'title': 'An Album'}]
            if path == 'track?artistId=1': return [dict(cls.track)]
            if path == 'trackfile/7': return {'path': str(cls.audio)}
            if path.startswith('trackdownload/search?'): return {'candidates': [{'id': 'candidate-1'}], 'warnings': []}
            if path == 'trackdownload/grab': return {'status': 'queued'}
            raise AssertionError(path)
        cls.mock = patch.object(server, 'upstream', side_effect=lidarr)
        cls.mock.start()
        with patch.dict(os.environ, {'CADENCE_ADMIN_PASSWORD': 'test-admin-password'}): server.initialize()
        cls.http = server.ThreadingHTTPServer(('127.0.0.1', 0), server.Handler)
        cls.thread = threading.Thread(target=cls.http.serve_forever, daemon=True)
        cls.thread.start()
        cls.base = 'http://127.0.0.1:' + str(cls.http.server_port)

    @classmethod
    def tearDownClass(cls):
        cls.http.shutdown(); cls.http.server_close(); cls.mock.stop(); cls.temp.cleanup()

    def client(self):
        return urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def call(self, client, path, payload=None, csrf=None, headers=None):
        headers = dict(headers or {})
        if payload is not None:
            headers['Content-Type'] = 'application/json'
            if csrf: headers['X-CSRF-Token'] = csrf
        request = urllib.request.Request(self.base + '/api/' + path, data=json.dumps(payload).encode() if payload is not None else None, headers=headers)
        try:
            with client.open(request) as response:
                body = response.read()
                return response.status, json.loads(body) if response.headers.get('Content-Type', '').startswith('application/json') else body, response.headers
        except urllib.error.HTTPError as error:
            return error.code, error.read(), error.headers

    def admin(self):
        client = self.client()
        code, user, _ = self.call(client, 'login', {'name': 'admin', 'password': 'test-admin-password'})
        self.assertEqual(code, 200)
        return client, user['csrf']

    def test_01_authentication_and_csrf(self):
        anon = self.client()
        self.assertEqual(self.call(anon, 'catalog')[0], 401)
        self.assertEqual(self.call(anon, 'login', {'name':'admin','password':'test-admin-password'}, headers={'Origin':'http://evil.example'})[0], 403)
        admin, csrf = self.admin()
        self.assertEqual(self.call(admin, 'playlists', {'name': 'Blocked'})[0], 403)
        self.assertEqual(self.call(admin, 'me')[1]['role'], 'admin')
        self.assertEqual(self.call(admin, 'logout', {}, csrf)[0], 200)
        self.assertEqual(self.call(admin, 'me')[0], 401)

    def test_02_request_approval_and_download(self):
        admin, csrf = self.admin()
        self.call(admin, 'users', {'name':'listener','password':'listener-password'}, csrf)
        user = self.client()
        token = self.call(user, 'login', {'name':'listener','password':'listener-password'})[1]['csrf']
        code, requests, _ = self.call(user, 'requests', {'trackId':11}, token)
        self.assertEqual(code, 200)
        self.assertEqual(requests[0]['status'], 'pending')
        request_id = requests[0]['id']
        self.assertEqual(self.call(user, 'requests', {'action':'approve','id':request_id}, token)[0], 403)
        self.assertEqual(self.call(user, 'sources?trackId=11')[0], 403)
        self.assertEqual(self.call(user, 'requests', {'trackId':11}, token)[0], 409)
        approved = self.call(admin, 'requests', {'action':'approve','id':request_id}, csrf)[1]
        self.assertEqual(approved[0]['status'], 'approved')
        self.assertEqual(self.call(admin, 'sources?trackId=11')[1]['candidates'][0]['id'], 'candidate-1')
        self.assertEqual(self.call(admin, 'sources', {'trackId':11,'candidateId':'candidate-1'}, csrf)[0], 200)
        self.assertEqual(self.call(user, 'requests')[1][0]['status'], 'downloading')
        self.track['hasFile'] = True
        server.CACHE['at'] = 0
        self.assertEqual(self.call(user, 'requests')[1][0]['status'], 'available')

    def test_03_playlist_ownership_and_identity_remapping(self):
        admin, csrf = self.admin()
        playlist = self.call(admin, 'playlists', {'name':'Night drive'}, csrf)[1][0]
        pid = playlist['id']
        self.assertEqual(self.call(admin, 'playlists', {'action':'add','id':pid,'trackId':11}, csrf)[1][0]['tracks'][0]['identity'], 'mb-song')
        self.call(admin, 'playlists', {'action':'add','id':pid,'trackId':11}, csrf)
        self.assertEqual(len(self.call(admin, 'playlists')[1][0]['tracks']), 1)
        self.assertEqual(self.call(admin, 'playlists', {'action':'reorder','id':pid,'identities':['invented']}, csrf)[0], 400)
        self.assertEqual(self.call(admin, 'playlists', {'action':'reorder','id':pid,'identities':['mb-song']}, csrf)[0], 200)
        self.call(admin, 'users', {'name':'other','password':'other-password'}, csrf)
        other = self.client()
        token = self.call(other, 'login', {'name':'other','password':'other-password'})[1]['csrf']
        self.assertEqual(self.call(other, 'playlists')[1], [])
        self.assertEqual(self.call(other, 'playlists', {'action':'delete','id':pid}, token)[0], 404)
        self.track['id'] = 22; server.CACHE['at'] = 0
        self.assertEqual(self.call(admin, 'playlists')[1][0]['tracks'][0]['id'], 22)
        self.assertEqual(self.call(admin, 'playlists', {'action':'remove','id':pid,'identity':'mb-song'}, csrf)[1][0]['tracks'], [])
        self.call(admin, 'playlists', {'action':'delete','id':pid}, csrf)
        self.assertEqual(self.call(admin, 'playlists')[1], [])

    def test_04_audio_ranges_and_path_containment(self):
        admin, _ = self.admin()
        code, data, headers = self.call(admin, 'stream/22', headers={'Range':'bytes=2-5'})
        self.assertEqual((code, data), (206, b'2345'))
        self.assertEqual(headers['Content-Range'], 'bytes 2-5/10')
        self.assertEqual(self.call(admin, 'stream/22', headers={'Range':'bytes=-3'})[1], b'789')
        self.assertEqual(self.call(admin, 'stream/22', headers={'Range':'bytes=100-200'})[0], 416)
        self.assertEqual(self.call(self.client(), 'stream/22')[0], 401)
        outside = server.DATA / 'outside.mp3'; outside.write_bytes(b'not-music')
        with patch.object(server, 'upstream', return_value={'path':str(outside)}):
            self.assertEqual(self.call(admin, 'stream/22')[0], 404)

    def test_05_static_app_and_security_headers(self):
        with urllib.request.urlopen(self.base) as response:
            self.assertIn(b'Cadence', response.read())
            self.assertIn("frame-ancestors 'none'", response.headers['Content-Security-Policy'])
        self.assertEqual(server.byte_range('bytes=0-', 10), (0, 9, True))
        with self.assertRaises(server.Problem): server.byte_range('bytes=1-3,5-6', 10)

if __name__ == '__main__': unittest.main(verbosity=2)
