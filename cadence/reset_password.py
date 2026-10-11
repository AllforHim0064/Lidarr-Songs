"""Interactive account recovery inside the Cadence container."""
import getpass
import server

name = input('Username: ').strip()
password = getpass.getpass('New password (at least 10 characters): ')
confirmation = getpass.getpass('Confirm new password: ')
if password != confirmation:
    raise SystemExit('Passwords do not match; no changes made.')
try:
    hashed = server.password_hash(password)
    with server.connect() as db:
        user = db.execute('SELECT id FROM users WHERE name=?', (name,)).fetchone()
        if not user:
            raise SystemExit('Account not found; no changes made.')
        db.execute('UPDATE users SET password=? WHERE id=?', (hashed, user['id']))
        db.execute('DELETE FROM sessions WHERE user_id=?', (user['id'],))
    print('Password reset; existing sessions revoked.')
except server.Problem as error:
    raise SystemExit(str(error)) from None
