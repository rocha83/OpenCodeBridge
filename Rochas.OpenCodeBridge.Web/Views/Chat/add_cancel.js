// Add cancel button and AbortController after sendBtn.innerHTML = 'Enviando...'
// Find the line: sendBtn.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>Enviando...';
// Add after it:
/*
  // Add Cancel button
  const cancelBtn = document.createElement('button');
  cancelBtn.type = 'button';
  cancelBtn.className = 'btn btn-sm btn-outline-danger ms-2';
  cancelBtn.id = 'cancelBtn';
  cancelBtn.innerHTML = '<i class="fas fa-times me-1"></i>Cancelar';
  cancelBtn.style.display = 'none';
  sendBtn.parentNode.insertBefore(cancelBtn, sendBtn.nextSibling);
  
  // AbortController for cancellation
  const abortController = new AbortController();
  cancelBtn.style.display = 'inline-block';
  cancelBtn.onclick = () => {
    abortController.abort();
    cancelBtn.style.display = 'none';
  };
*/
