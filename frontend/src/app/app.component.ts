import { Component, OnInit } from '@angular/core';
import * as QRCode from 'qrcode';
import { environment } from '../environments/environment';
import { CartLine, Product, StoreApiService, StoreOrder, AdminSummary } from './store-api.service';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.scss']
})
export class AppComponent implements OnInit {
  products: Product[] = [];
  cart: CartLine[] = [];
  searchTerm = '';
  activeCategory = 'All';
  cartOpen = false;
  adminOpen = false;
  adminAuthenticated = false;
  adminUserName = '';
  adminLoginUserName = '';
  adminLoginPassword = '';
  loggingAdminLogin = false;
  accountSettingsOpen = false;
  updatingAdminCredentials = false;
  currentAdminPassword = '';
  newAdminUserName = '';
  newAdminPassword = '';
  loadingProducts = true;
  submittingOrder = false;
  loadingAdmin = false;
  notice = '';
  noticeVisible = false;
  private noticeTimer: number | null = null;
  bagPulse = false;
  errorMessage = '';
  splashVisible = true;
  private readonly startTimestamp = Date.now();
  orderSuccessId: number | null = null;
  currentOrder: StoreOrder | null = null;
  paymentAccessToken = '';
  paymentTransactionReference = '';
  reportingUpiPayment = false;
  cancellingOrder = false;
  paymentStatusError = '';
  paymentReportMessage = '';
  upiCopyMessage = '';
  upiQrDataUrl = '';
  upiPaymentLink = '';
  qrGenerationError = '';
  orders: StoreOrder[] = [];
  selectedAdminOrder: StoreOrder | null = null;
  adminProducts: Product[] = [];
  savingProduct = false;
  editingProduct: Product | null = null;
  editProductDraft: Omit<Product, 'id' | 'imageUrl'> | null = null;
  editProductImageFile: File | null = null;
  editProductImagePreview = '';
  savingProductEdit = false;
  editProductError = '';
  deletingProductId: number | null = null;
  selectedProductImage: File | null = null;
  productImagePreviewUrl = '';
  newProduct: Omit<Product, 'id'> = {
    name: '',
    description: '',
    price: 0,
    image: '',
    category: '',
    stock: 0
  };
  summary: AdminSummary = { totalOrders: 0, revenue: 0, pendingPayments: 0, productCount: 0 };
  customerName = '';
  email = '';
  address = '';

  constructor(private storeApi: StoreApiService) { }

  ngOnInit(): void {
    this.restoreCart();
    this.restorePendingPayment();
    this.loadProducts();
  }

  get categories(): string[] {
    return ['All'].concat(this.products.map(product => product.category)
      .filter((category, index, all) => all.indexOf(category) === index));
  }

  get filteredProducts(): Product[] {
    const query = this.searchTerm.trim().toLowerCase();
    return this.products.filter(product =>
      (this.activeCategory === 'All' || product.category === this.activeCategory) &&
      (!query || (product.name + ' ' + product.description + ' ' + product.category).toLowerCase().indexOf(query) >= 0)
    );
  }

  get cartCount(): number {
    return this.cart.reduce((count, line) => count + line.quantity, 0);
  }

  get cartTotal(): number {
    return this.cart.reduce((total, line) => total + line.product.price * line.quantity, 0);
  }

  loadProducts(): void {
    this.loadingProducts = true;
    this.storeApi.getProducts().subscribe(products => {
      this.products = products;
      this.loadingProducts = false;
      this.hideSplash();
    }, error => {
      this.loadingProducts = false;
      this.errorMessage = this.messageFrom(error, 'Could not reach the store API. Start the backend and try again.');
      this.hideSplash();
    });
  }

  private hideSplash(): void {
    const remainingTime = 1500 - (Date.now() - this.startTimestamp);
    window.setTimeout(() => { this.splashVisible = false; }, remainingTime);
  }

  addToCart(product: Product): void {
    this.errorMessage = '';
    const existing = this.cart.find(line => line.product.id === product.id);
    if (existing) {
      if (existing.quantity >= product.stock) {
        this.errorMessage = 'That is all the available stock for ' + product.name + '.';
        return;
      }
      existing.quantity++;
    } else if (product.stock > 0) {
      this.cart.push({ product: product, quantity: 1 });
    }
    this.saveCart();
    this.notice = product.name + ' added to your bag';
    this.showNotice();
    this.pulseBag();
    this.cartOpen = true;
  }

  private showNotice(): void {
    this.noticeVisible = true;
    if (this.noticeTimer !== null) {
      window.clearTimeout(this.noticeTimer);
    }
    this.noticeTimer = window.setTimeout(() => {
      this.noticeVisible = false;
      this.noticeTimer = null;
    }, 3200);
  }

  private pulseBag(): void {
    this.bagPulse = false;
    window.setTimeout(() => {
      this.bagPulse = true;
      window.setTimeout(() => { this.bagPulse = false; }, 650);
    }, 0);
  }

  changeQuantity(productId: number, change: number): void {
    const line = this.cart.find(item => item.product.id === productId);
    if (!line) { return; }
    const nextQuantity = line.quantity + change;
    if (nextQuantity <= 0) {
      this.cart = this.cart.filter(item => item.product.id !== productId);
    } else if (nextQuantity <= line.product.stock) {
      line.quantity = nextQuantity;
    }
    this.saveCart();
  }

  removeFromCart(productId: number): void {
    this.cart = this.cart.filter(line => line.product.id !== productId);
    this.saveCart();
  }

  placeOrder(): void {
    if (!this.cart.length || this.submittingOrder) { return; }
    this.errorMessage = '';
    this.submittingOrder = true;
    this.storeApi.createOrder({
      customerName: this.customerName.trim(),
      email: this.email.trim(),
      address: this.address.trim(),
      items: this.cart.map(line => ({ productId: line.product.id, quantity: line.quantity }))
    }).subscribe(order => {
      this.orderSuccessId = order.id;
      this.currentOrder = order;
      this.paymentAccessToken = order.accessToken;
      this.paymentTransactionReference = '';
      this.paymentStatusError = '';
      this.paymentReportMessage = '';
      this.createUpiPaymentQr(order);
      this.savePendingPayment(order.id, order.accessToken);
      this.adminOpen = false;
      this.cartOpen = false;
      this.accountSettingsOpen = false;
      this.cart = [];
      this.customerName = '';
      this.email = '';
      this.address = '';
      this.saveCart();
      this.submittingOrder = false;
      this.loadProducts();
    }, error => {
      this.submittingOrder = false;
      this.errorMessage = this.messageFrom(error, 'We could not place your order. Please check your details and try again.');
      this.loadProducts();
    });
  }

  toggleAdmin(): void {
    if (this.adminOpen) {
      this.adminOpen = false;
      this.selectedAdminOrder = null;
      this.closeEditProduct();
      if (this.currentOrder && this.paymentAccessToken) {
        this.resumePayment(this.currentOrder.id, this.paymentAccessToken);
      }
      return;
    }

    this.adminOpen = true;
    this.cartOpen = false;
    this.orderSuccessId = null;
    this.errorMessage = '';
    if (this.adminAuthenticated) {
      this.loadAdminData();
    } else {
      this.restoreAdminSession();
    }
  }

  restoreAdminSession(): void {
    this.storeApi.getAdminSession().subscribe(session => {
      this.adminAuthenticated = true;
      this.adminUserName = session.userName;
      this.errorMessage = '';
      this.loadAdminData();
    }, () => {
      this.adminAuthenticated = false;
      this.adminUserName = '';
      this.storeApi.clearAdminToken();
    });
  }

  loginAdmin(): void {
    if (this.loggingAdminLogin) { return; }
    this.errorMessage = '';
    this.loggingAdminLogin = true;
    this.storeApi.adminLogin(this.adminLoginUserName.trim(), this.adminLoginPassword).subscribe(() => {
      this.adminLoginPassword = '';
      this.storeApi.getAdminSession().subscribe(session => {
        this.adminAuthenticated = true;
        this.adminUserName = session.userName;
        this.loggingAdminLogin = false;
        this.loadAdminData();
      }, () => {
        this.adminAuthenticated = false;
        this.loggingAdminLogin = false;
        this.storeApi.clearAdminToken();
        this.errorMessage = 'Sign-in succeeded, but the admin session could not be verified. Please try signing in again.';
      });
    }, error => {
      this.loggingAdminLogin = false;
      this.errorMessage = this.messageFrom(error, 'Could not sign in. Check your credentials and try again.');
    });
  }

  logoutAdmin(): void {
    this.storeApi.adminLogout().subscribe(() => {
      this.adminAuthenticated = false;
      this.selectedAdminOrder = null;
      this.closeEditProduct();
      this.adminUserName = '';
      this.adminLoginPassword = '';
      this.adminOpen = false;
      this.orders = [];
      this.adminProducts = [];
    }, error => {
      this.errorMessage = this.messageFrom(error, 'Could not sign out. Please try again.');
    });
  }

  openAccountSettings(): void {
    if (this.savingProductEdit) { return; }
    if (this.accountSettingsOpen) {
      this.closeAccountSettings();
      return;
    }
    this.selectedAdminOrder = null;
    this.closeEditProduct();
    this.currentAdminPassword = '';
    this.newAdminUserName = this.adminUserName;
    this.newAdminPassword = '';
    this.errorMessage = '';
    this.accountSettingsOpen = true;
  }

  closeAccountSettings(): void {
    if (this.updatingAdminCredentials) { return; }
    this.accountSettingsOpen = false;
    this.currentAdminPassword = '';
    this.newAdminPassword = '';
    this.errorMessage = '';
  }

  updateAdminCredentials(): void {
    if (this.updatingAdminCredentials) { return; }
    this.errorMessage = '';
    this.updatingAdminCredentials = true;
    this.storeApi.updateAdminCredentials({
      currentPassword: this.currentAdminPassword,
      newUserName: this.newAdminUserName.trim(),
      newPassword: this.newAdminPassword
    }).subscribe(session => {
      this.adminUserName = session.userName;
      this.currentAdminPassword = '';
      this.newAdminPassword = '';
      this.accountSettingsOpen = false;
      this.updatingAdminCredentials = false;
      this.notice = 'Admin username and password updated.';
    }, error => {
      this.updatingAdminCredentials = false;
      this.errorMessage = this.messageFrom(error, 'Could not update credentials. Check the current password and try again.');
    });
  }

  showShop(): void {
    this.adminOpen = false;
    this.cartOpen = false;
    this.orderSuccessId = null;
    this.errorMessage = '';
  }

  reopenPaymentDetails(): void {
    if (!this.currentOrder) { return; }
    this.orderSuccessId = this.currentOrder.id;
    if (this.paymentAccessToken) {
      this.refreshCustomerPaymentStatus();
    }
  }

  private savePendingPayment(orderId: number, accessToken: string): void {
    try {
      sessionStorage.setItem('sapana-current-payment', JSON.stringify({ orderId: orderId, accessToken: accessToken }));
    } catch (error) { /* Browser storage may be disabled. */ }
  }

  private clearPendingPayment(): void {
    try { sessionStorage.removeItem('sapana-current-payment'); } catch (error) { /* Browser storage may be disabled. */ }
  }

  private restorePendingPayment(): void {
    try {
      const saved = sessionStorage.getItem('sapana-current-payment');
      if (!saved) { return; }
      const payment = JSON.parse(saved) as { orderId?: number; accessToken?: string };
      if (typeof payment.orderId === 'number' && typeof payment.accessToken === 'string' && payment.accessToken) {
        this.resumePayment(payment.orderId, payment.accessToken);
      }
    } catch (error) {
      this.clearPendingPayment();
    }
  }

  get paymentCanBeReported(): boolean {
    return !!this.currentOrder && this.currentOrder.paymentStatus === 'Pending';
  }

  get paymentHasBeenReported(): boolean {
    return !!this.currentOrder && this.currentOrder.paymentStatus === 'Pending' &&
      !!this.currentOrder.paymentReportedAt && !!this.currentOrder.paymentReference;
  }

  resumePayment(orderId: number, accessToken: string): void {
    this.paymentStatusError = '';
    this.storeApi.getCustomerOrderStatus(orderId, accessToken).subscribe(order => {
      this.currentOrder = order;
      this.orderSuccessId = order.id;
      this.paymentAccessToken = accessToken;
      this.createUpiPaymentQr(order);
      this.adminOpen = false;
      this.cartOpen = false;
      if (['Paid', 'Failed', 'Refunded', 'Cancelled'].indexOf(order.paymentStatus) >= 0) {
        this.clearPendingPayment();
      }
    }, error => {
      this.paymentStatusError = this.messageFrom(error, 'This payment link is invalid or no longer available.');
    });
  }

  reportUpiPayment(): void {
    if (!this.currentOrder || !this.paymentAccessToken || !this.paymentCanBeReported ||
        this.reportingUpiPayment || this.cancellingOrder) { return; }
    this.reportingUpiPayment = true;
    this.paymentStatusError = '';
    this.paymentReportMessage = '';
    this.storeApi.reportUpiPayment(
      this.currentOrder.id,
      this.paymentAccessToken,
      this.paymentTransactionReference
    ).subscribe(order => {
      this.currentOrder = order;
      this.paymentTransactionReference = '';
      this.paymentReportMessage = 'Payment reported. Your order remains unverified until the store checks the bank transaction.';
      this.reportingUpiPayment = false;
    }, error => {
      this.reportingUpiPayment = false;
      this.paymentStatusError = this.messageFrom(error, 'Could not report the UPI reference. Please try again.');
    });
  }

  cancelOrder(): void {
    if (!this.currentOrder || !this.paymentAccessToken || this.cancellingOrder || this.reportingUpiPayment ||
        this.currentOrder.paymentStatus !== 'Pending' || this.currentOrder.paymentReportedAt ||
        !window.confirm('Cancel this unpaid order and release its reserved stock? Do not cancel if you have already sent money.')) {
      return;
    }

    this.cancellingOrder = true;
    this.paymentStatusError = '';
    this.storeApi.cancelOrder(this.currentOrder.id, this.paymentAccessToken).subscribe(order => {
      this.currentOrder = order;
      this.cancellingOrder = false;
      this.clearPendingPayment();
      this.loadProducts();
    }, error => {
      this.cancellingOrder = false;
      this.paymentStatusError = this.messageFrom(error, 'Could not cancel this order. Refresh its status and try again.');
    });
  }

  refreshCustomerPaymentStatus(): void {
    if (!this.currentOrder || !this.paymentAccessToken) { return; }
    this.resumePayment(this.currentOrder.id, this.paymentAccessToken);
  }

  async copyUpiId(): Promise<void> {
    try {
      await navigator.clipboard.writeText(environment.upiVpa);
      this.upiCopyMessage = 'UPI ID copied.';
    } catch (error) {
      this.upiCopyMessage = 'Copy unavailable. UPI ID: ' + environment.upiVpa;
    }
  }

  get upiConfigured(): boolean {
    return !!environment.upiVpa && !!environment.upiPayeeName;
  }

  get environmentUpiVpa(): string {
    return environment.upiVpa;
  }

  private createUpiPaymentQr(order: StoreOrder): void {
    this.upiQrDataUrl = '';
    this.qrGenerationError = '';
    if (!this.upiConfigured) { return; }

    const params = [
      'pa=' + encodeURIComponent(environment.upiVpa),
      'pn=' + encodeURIComponent(environment.upiPayeeName),
      'am=' + order.total.toFixed(2),
      'cu=INR',
      'tn=' + encodeURIComponent('Sapana Kitchen Katta order ' + order.id)
    ];
    this.upiPaymentLink = 'upi://pay?' + params.join('&');
    QRCode.toDataURL(this.upiPaymentLink, { width: 240, margin: 2, errorCorrectionLevel: 'M' })
      .then(dataUrl => { this.upiQrDataUrl = dataUrl; })
      .catch(() => { this.qrGenerationError = 'The payment QR could not be generated. Please use the UPI app link instead.'; });
  }

  loadAdminData(): void {
    this.loadingAdmin = true;
    this.storeApi.getSummary().subscribe(summary => {
      this.summary = summary;
      this.loadingAdmin = false;
    }, error => {
      this.loadingAdmin = false;
      this.handleAdminDataError(error, 'Could not load the admin dashboard.');
    });
    this.storeApi.getOrders().subscribe(orders => {
      this.orders = orders;
      if (this.selectedAdminOrder) {
        this.selectedAdminOrder = orders.find(order => order.id === this.selectedAdminOrder.id) || null;
      }
    }, error => {
      this.handleAdminDataError(error, 'Could not load recent orders.');
    });
    this.storeApi.getProducts().subscribe(products => {
      this.adminProducts = products;
    }, error => {
      this.handleAdminDataError(error, 'Could not load the product collection.');
    });
  }

  private handleAdminDataError(error: any, fallback: string): void {
    if (error.status === 401) {
      this.adminAuthenticated = false;
      this.selectedAdminOrder = null;
      this.closeEditProduct();
      this.adminUserName = '';
      this.storeApi.clearAdminToken();
      this.orders = [];
      this.adminProducts = [];
      this.errorMessage = 'Your admin session has expired or was revoked. Please sign in again.';
    } else {
      this.errorMessage = this.messageFrom(error, fallback);
    }
  }

  createProduct(): void {
    if (this.savingProduct) { return; }
    this.errorMessage = '';
    this.savingProduct = true;
    this.storeApi.createProduct({
      name: this.newProduct.name.trim(),
      description: this.newProduct.description.trim(),
      price: Number(this.newProduct.price),
      image: this.selectedProductImage ? '' : this.newProduct.image.trim(),
      category: this.newProduct.category.trim(),
      stock: Number(this.newProduct.stock)
    }, this.selectedProductImage || undefined).subscribe(product => {
      this.adminProducts.push(product);
      this.newProduct = { name: '', description: '', price: 0, image: '', category: '', stock: 0 };
      this.clearProductImage();
      this.savingProduct = false;
      this.notice = product.name + ' added to the collection.';
      this.loadProducts();
      this.loadAdminData();
    }, error => {
      this.savingProduct = false;
      this.errorMessage = this.messageFrom(error, 'Could not add this product. Check the details and try again.');
    });
  }

  onProductImageSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files && input.files.length ? input.files[0] : null;
    this.errorMessage = '';
    if (!file) { return; }
    const supportedTypes = ['image/jpeg', 'image/png', 'image/gif', 'image/webp'];
    if (supportedTypes.indexOf(file.type) < 0 || file.size > 5 * 1024 * 1024) {
      this.errorMessage = 'Choose a JPG, PNG, GIF, or WebP image smaller than 5 MB.';
      input.value = '';
      return;
    }
    this.clearProductImage();
    this.selectedProductImage = file;
    const reader = new FileReader();
    reader.onload = () => {
      if (this.selectedProductImage === file && typeof reader.result === 'string') {
        this.productImagePreviewUrl = reader.result;
      }
    };
    reader.onerror = () => {
      if (this.selectedProductImage === file) {
        this.errorMessage = 'Could not preview this image. Please choose another file.';
        this.clearProductImage();
      }
    };
    reader.readAsDataURL(file);
  }

  clearProductImage(): void {
    this.productImagePreviewUrl = '';
    this.selectedProductImage = null;
  }

  openEditProduct(product: Product): void {
    this.editingProduct = product;
    this.editProductDraft = {
      name: product.name,
      description: product.description,
      price: product.price,
      stock: product.stock,
      category: product.category,
      image: product.image
    };
    this.editProductImageFile = null;
    this.editProductImagePreview = product.imageUrl || '';
    this.editProductError = '';
  }

  closeEditProduct(): void {
    if (this.savingProductEdit) { return; }
    this.editingProduct = null;
    this.editProductDraft = null;
    this.editProductImageFile = null;
    this.editProductImagePreview = '';
    this.editProductError = '';
  }

  onEditProductImageSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files && input.files.length ? input.files[0] : null;
    this.editProductError = '';
    if (!file || !this.editProductDraft) { return; }
    if (['image/jpeg', 'image/png', 'image/gif', 'image/webp'].indexOf(file.type) < 0 || file.size > 5 * 1024 * 1024 || file.size === 0) {
      this.editProductError = 'Choose a JPG, PNG, GIF, or WebP image smaller than 5 MB.';
      input.value = '';
      return;
    }
    this.editProductImageFile = file;
    this.editProductDraft.image = '';
    this.editProductImagePreview = '';
    const reader = new FileReader();
    reader.onload = () => {
      if (this.editProductImageFile === file && typeof reader.result === 'string') {
        this.editProductImagePreview = reader.result;
      }
    };
    reader.onerror = () => {
      if (this.editProductImageFile === file) {
        this.editProductError = 'Could not preview this image. Please choose another file.';
        this.clearEditProductImage();
        input.value = '';
      }
    };
    reader.readAsDataURL(file);
  }

  clearEditProductImage(): void {
    this.editProductImageFile = null;
    this.editProductImagePreview = this.editingProduct && this.editingProduct.imageUrl || '';
    if (this.editProductDraft && this.editingProduct) {
      this.editProductDraft.image = this.editingProduct.image;
    }
  }

  saveProductEdit(): void {
    if (!this.editingProduct || !this.editProductDraft || this.savingProductEdit) { return; }
    const id = this.editingProduct.id;
    const draft = this.editProductDraft;
    this.editProductError = '';
    this.savingProductEdit = true;
    this.storeApi.updateProduct(id, {
      name: draft.name.trim(),
      description: draft.description.trim(),
      price: Number(draft.price),
      stock: Number(draft.stock),
      category: draft.category.trim(),
      image: this.editProductImageFile ? '' : draft.image.trim()
    }, this.editProductImageFile || undefined).subscribe(updated => {
      this.savingProductEdit = false;
      this.adminProducts = this.adminProducts.map(product => product.id === id ? updated : product);
      this.cart = this.cart.map(line => line.product.id === id ? { ...line, product: updated } : line);
      this.saveCart();
      this.closeEditProduct();
      this.notice = updated.name + ' updated.';
      this.loadProducts();
    }, error => {
      this.savingProductEdit = false;
      if (error.status === 401) {
        this.handleAdminDataError(error, 'Could not update the product.');
      } else {
        this.editProductError = this.messageFrom(error, 'Could not update the product. Please check the details and try again.');
      }
    });
  }

  deleteProduct(product: Product): void {
    if (this.deletingProductId !== null || !window.confirm('Remove ' + product.name + ' from the collection?')) { return; }
    this.errorMessage = '';
    this.deletingProductId = product.id;
    this.storeApi.deleteProduct(product.id).subscribe(() => {
      this.adminProducts = this.adminProducts.filter(item => item.id !== product.id);
      this.cart = this.cart.filter(line => line.product.id !== product.id);
      this.saveCart();
      this.deletingProductId = null;
      this.notice = product.name + ' removed from the collection.';
      this.loadProducts();
      this.loadAdminData();
    }, error => {
      this.deletingProductId = null;
      this.errorMessage = this.messageFrom(error, 'Could not remove this product. Please try again.');
    });
  }

  setPaymentStatus(order: StoreOrder, status: string): void {
    this.storeApi.updatePaymentStatus(order.id, status).subscribe(updated => {
      order.paymentStatus = updated.paymentStatus;
      this.loadAdminData();
    }, error => {
      this.errorMessage = this.messageFrom(error, 'Could not update the payment status.');
    });
  }

  openAdminOrderDetails(order: StoreOrder): void {
    this.selectedAdminOrder = order;
  }

  closeAdminOrderDetails(): void {
    this.selectedAdminOrder = null;
  }

  trackProduct(index: number, product: Product): number { return product.id; }
  trackOrder(index: number, order: StoreOrder): number { return order.id; }

  private saveCart(): void {
    try {
      localStorage.setItem('sapana-kitchen-katta-cart', JSON.stringify(this.cart));
      localStorage.removeItem('fieldnotes-cart');
    } catch (error) { /* Storage may be unavailable. */ }
  }

  private restoreCart(): void {
    try {
      const savedCart = localStorage.getItem('sapana-kitchen-katta-cart') || localStorage.getItem('fieldnotes-cart') || '[]';
      const saved = JSON.parse(savedCart) as CartLine[];
      if (Array.isArray(saved)) { this.cart = saved; }
    } catch (error) {
      this.cart = [];
    }
  }

  private messageFrom(error: any, fallback: string): string {
    return error && error.error && error.error.message ? error.error.message : fallback;
  }

}
