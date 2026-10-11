import PropTypes from 'prop-types';
import React, { useEffect, useState } from 'react';
import Button from 'Components/Link/Button';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { sizes } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import formatBytes from 'Utilities/Number/formatBytes';
import styles from './TrackDownloadModal.css';

function TrackDownloadModal({ isOpen, trackId, onModalClose }) {
  const [result, setResult] = useState(null);
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);
  const [fallback, setFallback] = useState(true);
  const [queued, setQueued] = useState(false);
  const [jobs, setJobs] = useState([]);

  useEffect(() => {
    if (!isOpen) {
      return undefined;
    }

    let active = true;
    setResult(null);
    setError(null);
    setQueued(false);
    const { request, abortRequest } = createAjaxRequest({
      url: '/trackdownload/search',
      dataType: 'json',
      data: { trackId, albumFallback: fallback }
    });
    request.then((data) => {
      if (active) {
        setResult(data);
      }
    }, (xhr) => {
      if (active) {
        setError(xhr.responseJSON?.message || 'Track search failed. Check the Lidarr logs.');
      }
    });
    return () => {
      active = false;
      abortRequest();
    };
  }, [isOpen, trackId, fallback]);

  useEffect(() => {
    if (!isOpen) {
      return undefined;
    }

    let active = true;
    let abort = () => {};
    const refresh = () => {
      const { request, abortRequest } = createAjaxRequest({ url: '/trackdownload/soulseek', dataType: 'json' });
      abort = abortRequest;
      request.then((data) => {
        if (active) {
          setJobs(data.filter((job) => job.trackId === trackId));
        }
      }, () => {});
    };
    refresh();
    const timer = setInterval(refresh, 10000);
    return () => {
      active = false;
      clearInterval(timer);
      abort();
    };
  }, [isOpen, trackId, queued]);

  const grab = (candidate) => {
    setBusy(true);
    setError(null);
    const { request } = createAjaxRequest({
      url: '/trackdownload/grab',
      method: 'POST',
      dataType: 'json',
      data: JSON.stringify({ trackId, candidateId: candidate.id })
    });
    request.then(() => {
      setQueued(true);
      setBusy(false);
    }, (xhr) => {
      setBusy(false);
      setError(typeof xhr.responseJSON === 'string' ? xhr.responseJSON : (xhr.responseJSON?.message || xhr.responseText || 'Download failed.'));
    });
  };

  return (
    <Modal isOpen={isOpen} size={sizes.LARGE}
      onModalClose={onModalClose}
    >
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>Download this song</ModalHeader>
        <ModalBody>
          <p>Only this song will be added to your library. Album downloads are kept until the song is imported successfully and any torrent seeding requirements are met.</p>
          <label className={styles.option}>
            <input type="checkbox" checked={fallback}
              disabled={busy} onChange={(event) => setFallback(event.target.checked)}
            />
            Include whole albums as a fallback
          </label>
          {error && <p role="alert">{error}</p>}
          {queued && <p role="status">Download queued. Torrent and Usenet downloads appear in Activity; Soulseek status appears below.</p>}
          {!result && !error && <p role="status">Searching song sources and album fallbacks…</p>}
          {result && result.warnings.map((warning) => <p key={warning}>{warning}</p>)}
          {result && result.candidates.length === 0 && <p>No matching downloads were found.</p>}
          {result && (
            <table className={styles.results}>
              <thead><tr><th>Release / file</th><th>Source</th><th>Download</th><th>Size</th><th /></tr></thead>
              <tbody>
                {result.candidates.map((candidate) => (
                  <tr key={candidate.id}>
                    <td>{candidate.title}<div>{candidate.quality}</div>{candidate.rejection && <div>{candidate.rejection}</div>}</td>
                    <td>{candidate.source}</td>
                    <td>{candidate.albumFallback ? 'Album fallback' : 'Song search result'}</td>
                    <td>{formatBytes(candidate.size)}</td>
                    <td><Button isDisabled={busy || queued || !!candidate.rejection} onPress={() => grab(candidate)}>Download</Button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {jobs.map((job) => <p key={job.id}>Soulseek: {job.status}{job.error ? ` — ${job.error}` : ''}</p>)}
        </ModalBody>
        <ModalFooter><Button onPress={onModalClose}>Close</Button></ModalFooter>
      </ModalContent>
    </Modal>
  );
}

TrackDownloadModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  trackId: PropTypes.number.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default TrackDownloadModal;
