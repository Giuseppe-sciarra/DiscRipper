# Collegamento al CRM Tastiere Digitali

Il programma parla con il CRM con **le stesse API di VHSCapture** (`/api/cattura/*`) e il suo tipo di lavoro:
il blocco **Riversaggio / Backup** della scheda del cliente, quando il supporto è quello di questo programma
(Tape2MP3 → musicassette, DiscRipper → CD; la quantità = quanti pezzi).

## Come si collega
1. Nel CRM: Controllo PC → 🔑 della postazione → copia il token (lo stesso di VHSCapture su quel PC va bene).
2. Nel programma: ⚙ nella banda in alto → indirizzo del CRM (https://crm.tastieredigitali.it), token, «Prova collegamento», Salva.

## Come lavora
- Alla partenza (REC in Tape2MP3, Estrai in DiscRipper) chiede **per quale cliente**: le schede con pezzi ancora da fare;
  in cima «▶ Continua con …» se c'era un lavoro aperto.
- Nella Coda del CRM compare la riga dal vivo: «📻 PC · Rossi · riversa la musicassetta 1ª di 3» / «💽 PC · Rossi · riversa il CD 1º di 2».
- A fine lavoro (export in Tape2MP3, fine estrazione in DiscRipper): **Fatto, conta** · **Scarta** (il pezzo non si fa: il
  totale scende, il prezzo si ricalcola) · **Non contare**. Annullato o errore → non si conta niente.
- I pezzi contano nel totale dei supporti della scheda: finiti tutti, la scheda diventa «pronta». **🔢 Conteggio** corregge fatti/totali.
- Se la rete salta, gli eventi restano in coda su disco e partono da soli; nessun evento viene contato due volte.
