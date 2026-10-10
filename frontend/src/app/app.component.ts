import { Component, ElementRef, HostListener, NgZone, OnInit, ViewChild } from '@angular/core';
import { animate, group, query, style, transition, trigger } from '@angular/animations';
import * as QRCode from 'qrcode';
import { environment } from '../environments/environment';
import { CartLine, Product, StoreApiService, StoreOrder, AdminSummary, CustomerProfile } from './store-api.service';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.scss'],
  animations: [
    trigger('pageEnter', [
      transition(':enter', [
        style({ opacity: 0, transform: 'translateY(10px)' }),
        animate('260ms cubic-bezier(.2,.8,.2,1)', style({ opacity: 1, transform: 'translateY(0)' }))
      ])
    ]),
    trigger('dialogMotion', [
      transition(':enter', [
        style({ opacity: 0 }),
        group([
          animate('210ms ease-out', style({ opacity: 1 })),
          query('section', [
            style({ opacity: 0, transform: 'translateY(12px) scale(.98)' }),
            animate('250ms cubic-bezier(.2,.8,.2,1)', style({ opacity: 1, transform: 'translateY(0) scale(1)' }))
          ], { optional: true })
        ])
      ]),
      transition(':leave', [
        group([
          animate('170ms ease-in', style({ opacity: 0 })),
          query('section', [
            animate('170ms ease-in', style({ opacity: 0, transform: 'translateY(8px) scale(.98)' }))
          ], { optional: true })
        ])
      ])
    ])
  ]
})
export class AppComponent implements OnInit {
  readonly reduceMotion = typeof window !== 'undefined' && window.matchMedia &&
    window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  products: Product[] = [];
  cart: CartLine[] = [];
  selectedProduct: Product | null = null;
  animatedProductId: number | null = null;
  private productAnimationTimer: number | null = null;
  searchTerm = '';
  activeCategory = 'All';
  cartOpen = false;
  adminOpen = false;
  customerPage: 'shop' | 'orders' | 'details' = 'shop';
  customerProfile: CustomerProfile | null = null;
  guestCheckoutSelected = false;
  checkingCustomerSession = false;
  customerLoginOpen = false;
  customerMenuOpen = false;
  customerAuthBusy = false;
  customerSigningOut = false;
  customerAuthError = '';
  googleClientId = '';
  customerOrders: StoreOrder[] = [];
  customerOrdersPage = 0;
  customerOrdersTotal = 0;
  customerOrdersLoading = false;
  customerOrdersError = '';
  savingCustomerDetails = false;
  customerDetailsMessage = '';
  customerDetailsName = '';
  customerDetailsAddress = '';
  private googleScriptPromise: Promise<void> | null = null;
  private googleButtonElement?: HTMLElement;
  @ViewChild('googleButton', { static: false })
  set googleButton(button: ElementRef<HTMLElement> | undefined) {
    this.googleButtonElement = button && button.nativeElement;
    if (this.googleButtonElement) { this.renderGoogleButton(this.googleButtonElement); }
  }
  adminAuthenticated = false;
  checkingAdminSession = false;
  private adminSessionRequestId = 0;
  adminUserName = '';
  adminLoginUserName = '';
  adminLoginPassword = '';
  loggingAdminLogin = false;
  adminSigningOut = false;
  accountSettingsOpen = false;
  updatingAdminCredentials = false;
  currentAdminPassword = '';
  newAdminUserName = '';
  newAdminPassword = '';
  loadingProducts = true;
  submittingOrder = false;
  paymentMethod: 'UPI' | 'Razorpay' = 'UPI';
  razorpayAvailable = false;
  openingRazorpay = false;
  checkingRazorpayStatus = false;
  private razorpayStatusTimer: number | null = null;
  private razorpayCheckId = 0;
  private razorpayScriptPromise: Promise<void> | null = null;
  loadingAdmin = false;
  notice = '';
  noticeVisible = false;
  private noticeTimer: number | null = null;
  adminToastMessage = '';
  private adminToastTimer: number | null = null;
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
  ordersPage = 1;
  ordersPageSize = 15;
  ordersTotal = 0;
  loadingOrders = false;
  private ordersRequestId = 0;
  selectedAdminOrder: StoreOrder | null = null;
  dispatchTrackingId = '';
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

  constructor(private storeApi: StoreApiService, private zone: NgZone) { }

  ngOnInit(): void {
    this.restoreCart();
    this.restorePendingPayment();
    this.loadProducts();
    this.syncCustomerPage();
    this.storeApi.googleConfig().subscribe(config => {
      this.googleClientId = config.clientId;
      if (this.googleButtonElement) { this.renderGoogleButton(this.googleButtonElement); }
    });
    if (this.storeApi.hasCustomerSession()) {
      this.checkingCustomerSession = true;
      this.storeApi.customerMe().subscribe(customer => {
        this.checkingCustomerSession = false;
        this.setCustomer(customer);
        if (this.customerPage === 'orders') { this.loadMyOrders(); }
      }, () => {
        this.checkingCustomerSession = false;
        this.storeApi.clearCustomerSession();
      });
    }
    this.storeApi.razorpayAvailability().subscribe(result => { this.razorpayAvailable = result.enabled; }, () => { this.razorpayAvailable = false; });
  }

  @HostListener('window:hashchange')
  @HostListener('window:popstate')
  syncCustomerPage(): void {
    this.customerPage = window.location.hash === '#/my-orders' ? 'orders' :
      window.location.hash === '#/my-details' ? 'details' : 'shop';
    if (this.customerPage === 'orders' && this.customerProfile) { this.loadMyOrders(); }
    this.customerMenuOpen = false;
  }

  goToCustomerPage(page: 'shop' | 'orders' | 'details'): void {
    this.adminOpen = false;
    this.customerLoginOpen = false;
    this.customerMenuOpen = false;
    this.cartOpen = false;
    this.orderSuccessId = null;
    if (page === 'shop') {
      window.history.pushState(null, '', window.location.pathname + window.location.search);
      this.customerPage = 'shop';
    } else {
      window.location.hash = page === 'orders' ? '/my-orders' : '/my-details';
      this.syncCustomerPage();
    }
    window.scrollTo(0, 0);
  }

  openCustomerLogin(): void {
    this.customerLoginOpen = true;
    this.customerAuthError = '';
    this.customerMenuOpen = false;
    if (!this.googleClientId) {
      this.customerAuthError = 'Google sign-in is not configured yet. Guest checkout is still available.';
    }
  }

  private renderGoogleButton(button: HTMLElement): void {
    if (!this.customerLoginOpen || !this.googleClientId) { return; }
    this.customerAuthError = '';
    this.loadGoogleScript().then(() => this.zone.run(() => {
      if (!this.customerLoginOpen || this.googleButtonElement !== button) { return; }
      const google = (window as any).google;
      google.accounts.id.initialize({
        client_id: this.googleClientId,
        callback: (response: any) => this.zone.run(() => this.finishGoogleLogin(response.credential))
      });
      button.innerHTML = '';
      google.accounts.id.renderButton(button,
        { theme: 'outline', size: 'large', text: 'continue_with', width: 270 });
    })).catch(() => this.zone.run(() => {
      if (this.customerLoginOpen && this.googleButtonElement === button) {
        this.customerAuthError = 'Google sign-in could not load. Guest checkout is still available.';
      }
    }));
  }

  private loadGoogleScript(): Promise<void> {
    if ((window as any).google && (window as any).google.accounts) { return Promise.resolve(); }
    if (!this.googleScriptPromise) {
      this.googleScriptPromise = new Promise<void>((resolve, reject) => {
        const script = document.createElement('script');
        script.src = 'https://accounts.google.com/gsi/client';
        script.async = true;
        script.onload = () => resolve();
        script.onerror = () => { script.remove(); this.googleScriptPromise = null; reject(); };
        document.head.appendChild(script);
      });
    }
    return this.googleScriptPromise;
  }

  private finishGoogleLogin(credential: string): void {
    if (!credential || this.customerAuthBusy) { return; }
    this.customerAuthBusy = true;
    this.customerAuthError = '';
    this.storeApi.customerGoogleLogin(credential).subscribe(result => {
      this.customerAuthBusy = false;
      this.customerLoginOpen = false;
      this.guestCheckoutSelected = false;
      this.setCustomer(result.customer);
      if (this.customerPage === 'orders') { this.loadMyOrders(); }
    }, error => {
      this.customerAuthBusy = false;
      this.customerAuthError = this.messageFrom(error, 'Google sign-in failed. Please try again.');
    });
  }

  private setCustomer(customer: CustomerProfile): void {
    this.customerProfile = customer;
    this.customerDetailsName = customer.name;
    this.customerDetailsAddress = customer.deliveryAddress || '';
    if (!this.customerName) { this.customerName = customer.name; }
    if (!this.email) { this.email = customer.email; }
    if (!this.address) { this.address = customer.deliveryAddress || ''; }
  }

  customerLogout(): void {
    if (this.customerSigningOut) { return; }
    this.customerSigningOut = true;
    this.storeApi.customerLogout().subscribe(() => this.finishCustomerLogout(), () => this.finishCustomerLogout());
  }

  private finishCustomerLogout(): void {
    this.customerSigningOut = false;
    this.storeApi.clearCustomerSession();
    this.customerProfile = null;
    this.customerOrders = [];
    this.guestCheckoutSelected = false;
    this.customerName = '';
    this.email = '';
    this.address = '';
    this.goToCustomerPage('shop');
  }

  loadMyOrders(page: number = 1): void {
    if (!this.customerProfile) { return; }
    this.customerOrdersLoading = true;
    this.customerOrdersError = '';
    this.storeApi.myOrders(page).subscribe(result => {
      this.customerOrders = page === 1 ? result.items : this.customerOrders.concat(result.items);
      this.customerOrdersPage = result.page;
      this.customerOrdersTotal = result.totalCount;
      this.customerOrdersLoading = false;
    }, error => {
      this.customerOrdersLoading = false;
      this.customerOrdersError = this.messageFrom(error, 'Could not load your orders.');
      if (error.status === 401) { this.storeApi.clearCustomerSession(); this.customerProfile = null; }
    });
  }

  customerOrdersContain(orderId: number): boolean {
    return this.customerOrders.some(order => order.id === orderId);
  }

  claimCurrentGuestOrder(): void {
    if (!this.currentOrder || !this.paymentAccessToken || !this.customerProfile) { return; }
    this.storeApi.claimGuestOrder(this.currentOrder.id, this.paymentAccessToken).subscribe(() => this.loadMyOrders(), error => {
      this.customerOrdersError = this.messageFrom(error, 'Could not link this order.');
    });
  }

  saveCustomerDetails(): void {
    if (this.savingCustomerDetails || !this.customerProfile) { return; }
    this.savingCustomerDetails = true;
    this.customerDetailsMessage = '';
    this.storeApi.updateCustomerDetails(this.customerDetailsName.trim(), this.customerDetailsAddress.trim()).subscribe(customer => {
      this.savingCustomerDetails = false;
      this.setCustomer(customer);
      this.customerDetailsMessage = 'Details saved.';
    }, error => {
      this.savingCustomerDetails = false;
      this.customerDetailsMessage = this.messageFrom(error, 'Could not save your details.');
    });
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

  cartQuantity(productId: number): number {
    const line = this.cart.find(item => item.product.id === productId);
    return line ? line.quantity : 0;
  }

  openProductDetails(product: Product): void {
    this.selectedProduct = product;
  }

  closeProductDetails(): void {
    this.selectedProduct = null;
  }

  openBagFromProduct(): void {
    this.closeProductDetails();
    this.cartOpen = true;
  }

  continueAsGuest(): void {
    this.customerLoginOpen = false;
    this.guestCheckoutSelected = true;
    if (!this.customerProfile && this.storeApi.hasCustomerSession()) {
      this.storeApi.clearCustomerSession();
    }
    if (this.customerPage !== 'shop') { this.goToCustomerPage('shop'); }
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.selectedProduct) { this.closeProductDetails(); }
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
    } else {
      return;
    }
    this.saveCart();
    this.notice = product.name + ' added to your bag';
    this.showNotice();
    this.pulseBag();
    this.animateProduct(product.id);
  }

  private animateProduct(productId: number): void {
    if (this.productAnimationTimer !== null) {
      window.clearTimeout(this.productAnimationTimer);
    }
    this.animatedProductId = null;
    this.productAnimationTimer = window.setTimeout(() => {
      this.animatedProductId = productId;
      this.productAnimationTimer = window.setTimeout(() => {
        this.animatedProductId = null;
        this.productAnimationTimer = null;
      }, 480);
    }, 0);
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

  private showAdminToast(message: string): void {
    if (this.adminToastTimer !== null) {
      window.clearTimeout(this.adminToastTimer);
    }
    this.adminToastMessage = message;
    this.adminToastTimer = window.setTimeout(() => this.dismissAdminToast(), 4000);
  }

  dismissAdminToast(): void {
    if (this.adminToastTimer !== null) {
      window.clearTimeout(this.adminToastTimer);
      this.adminToastTimer = null;
    }
    this.adminToastMessage = '';
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
      if (change > 0) {
        this.pulseBag();
        this.animateProduct(productId);
      }
    } else {
      this.errorMessage = 'That is all the available stock for ' + line.product.name + '.';
      return;
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
      paymentMethod: this.paymentMethod,
      items: this.cart.map(line => ({ productId: line.product.id, quantity: line.quantity }))
    }).subscribe(order => {
      this.orderSuccessId = order.id;
      this.currentOrder = order;
      this.paymentAccessToken = order.accessToken;
      this.paymentTransactionReference = '';
      this.paymentStatusError = '';
      this.paymentReportMessage = '';
      if (order.paymentMethod !== 'Razorpay') { this.createUpiPaymentQr(order); }
      this.savePendingPayment(order.id, order.accessToken);
      this.adminOpen = false;
      this.cartOpen = false;
      this.accountSettingsOpen = false;
      this.cart = [];
      this.guestCheckoutSelected = false;
      this.customerName = '';
      this.email = '';
      this.address = '';
      this.saveCart();
      this.submittingOrder = false;
      this.loadProducts();
      if (this.customerProfile) { this.loadMyOrders(); }
      if (order.paymentMethod === 'Razorpay') { this.payWithRazorpay(); }
    }, error => {
      this.submittingOrder = false;
      this.errorMessage = this.messageFrom(error, 'We could not place your order. Please check your details and try again.');
      this.loadProducts();
    });
  }

  toggleAdmin(): void {
    this.closeProductDetails();
    this.customerLoginOpen = false;
    if (!this.adminOpen && this.customerPage !== 'shop') { this.goToCustomerPage('shop'); }
    if (this.adminOpen) {
      this.adminOpen = false;
      this.checkingAdminSession = false;
      this.adminSessionRequestId++;
      this.dismissAdminToast();
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
    const requestId = ++this.adminSessionRequestId;
    this.checkingAdminSession = true;
    this.storeApi.getAdminSession().subscribe(session => {
      if (requestId !== this.adminSessionRequestId || !this.adminOpen) { return; }
      this.checkingAdminSession = false;
      this.adminAuthenticated = true;
      this.adminUserName = session.userName;
      this.errorMessage = '';
      this.loadAdminData();
    }, () => {
      if (requestId !== this.adminSessionRequestId || !this.adminOpen) { return; }
      this.checkingAdminSession = false;
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
    if (this.adminSigningOut) { return; }
    this.adminSigningOut = true;
    this.storeApi.adminLogout().subscribe(() => {
      this.adminSigningOut = false;
      this.adminAuthenticated = false;
      this.dismissAdminToast();
      this.selectedAdminOrder = null;
      this.closeEditProduct();
      this.adminUserName = '';
      this.adminLoginPassword = '';
      this.adminOpen = false;
      this.orders = [];
      this.adminProducts = [];
    }, error => {
      this.adminSigningOut = false;
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
      this.showAdminToast('Admin login details updated.');
    }, error => {
      this.updatingAdminCredentials = false;
      this.errorMessage = this.messageFrom(error, 'Could not update credentials. Check the current password and try again.');
    });
  }

  showShop(): void {
    this.adminOpen = false;
    this.customerPage = 'shop';
    this.cartOpen = false;
    this.orderSuccessId = null;
    this.errorMessage = '';
  }

  reloadHome(event: MouseEvent): void {
    event.preventDefault();
    window.history.replaceState(null, '', '/');
    window.location.reload();
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
    return !!this.currentOrder && this.currentOrder.paymentMethod !== 'Razorpay' && this.currentOrder.paymentStatus === 'Pending';
  }

  payWithRazorpay(): void {
    if (!this.currentOrder || !this.paymentAccessToken || this.currentOrder.paymentMethod !== 'Razorpay' ||
        this.currentOrder.paymentStatus !== 'Pending' || this.openingRazorpay) { return; }
    const order = this.currentOrder;
    this.stopRazorpayStatusChecks();
    this.openingRazorpay = true;
    this.paymentStatusError = '';
    this.paymentReportMessage = '';
    this.storeApi.startRazorpay(order.id, this.paymentAccessToken).subscribe(checkout => {
      this.loadRazorpayScript().then(() => {
        this.zone.run(() => {
          this.openingRazorpay = false;
          const Razorpay = (window as any).Razorpay;
          let checkoutSucceeded = false;
          let checkoutFailed = false;
          const widget = new Razorpay({
            key: checkout.keyId,
            amount: checkout.amount,
            currency: checkout.currency,
            order_id: checkout.orderId,
            name: 'Sapana’s Kitchen Katta',
            description: 'Test payment for order #' + order.id,
            theme: { color: '#bd2b1d' },
            handler: (result: any) => this.zone.run(() => {
              checkoutSucceeded = true;
              this.stopRazorpayStatusChecks();
              this.openingRazorpay = true;
              this.paymentStatusError = '';
              if (result.razorpay_order_id !== checkout.orderId || !result.razorpay_payment_id || !result.razorpay_signature) {
                this.openingRazorpay = false;
                this.paymentStatusError = 'Checking payment with Razorpay…';
                this.startRazorpayStatusChecks(order.id);
                return;
              }
              this.storeApi.verifyRazorpay(order.id, this.paymentAccessToken, result.razorpay_payment_id,
                result.razorpay_signature).subscribe(() => {
                this.openingRazorpay = false;
                this.markRazorpayPaid(order.id);
              }, error => {
                this.openingRazorpay = false;
                this.paymentStatusError = this.messageFrom(error, 'Waiting for Razorpay to confirm the captured payment.');
                this.startRazorpayStatusChecks(order.id);
              });
            }),
            modal: {
              ondismiss: () => this.zone.run(() => {
                if (!checkoutSucceeded && !checkoutFailed) {
                  this.paymentStatusError = 'Checkout closed. Checking for a completed payment; you can retry if it remains Pending.';
                  this.startRazorpayStatusChecks(order.id);
                }
              })
            }
          });
          widget.on('payment.failed', () => this.zone.run(() => {
            checkoutFailed = true;
            this.paymentStatusError = 'Razorpay test payment failed. No payment is marked Paid; you can retry.';
            this.startRazorpayStatusChecks(order.id);
          }));
          widget.open();
        });
      }).catch(() => this.zone.run(() => {
        this.openingRazorpay = false;
        this.paymentStatusError = 'Razorpay checkout could not load. Try again.';
      }));
    }, error => {
      this.openingRazorpay = false;
      this.paymentStatusError = this.messageFrom(error, 'Razorpay checkout is unavailable. Try again.');
    });
  }

  private markRazorpayPaid(orderId: number): void {
    if (!this.currentOrder || this.currentOrder.id !== orderId) { return; }
    this.stopRazorpayStatusChecks();
    this.currentOrder.paymentStatus = 'Paid';
    this.paymentStatusError = '';
    this.paymentReportMessage = 'Razorpay test payment captured and verified.';
    this.clearPendingPayment();
  }

  private stopRazorpayStatusChecks(): void {
    this.razorpayCheckId++;
    if (this.razorpayStatusTimer !== null) {
      window.clearTimeout(this.razorpayStatusTimer);
      this.razorpayStatusTimer = null;
    }
    this.checkingRazorpayStatus = false;
  }

  private startRazorpayStatusChecks(orderId: number): void {
    if (!this.currentOrder || this.currentOrder.id !== orderId || this.currentOrder.paymentStatus !== 'Pending') { return; }
    this.stopRazorpayStatusChecks();
    const checkId = this.razorpayCheckId;
    const accessToken = this.paymentAccessToken;
    this.checkingRazorpayStatus = true;
    const check = (attempt: number) => {
      this.storeApi.getCustomerOrderStatus(orderId, accessToken).subscribe(status => {
        if (checkId !== this.razorpayCheckId || !this.currentOrder || this.currentOrder.id !== orderId) { return; }
        if (status.paymentStatus === 'Paid') {
          this.markRazorpayPaid(orderId);
        } else if (attempt < 10 && status.paymentStatus === 'Pending') {
          this.razorpayStatusTimer = window.setTimeout(() => check(attempt + 1), 2000);
        } else {
          this.checkingRazorpayStatus = false;
          this.currentOrder = status;
        }
      }, () => {
        if (checkId !== this.razorpayCheckId) { return; }
        if (attempt < 10) {
          this.razorpayStatusTimer = window.setTimeout(() => check(attempt + 1), 2000);
        } else {
          this.checkingRazorpayStatus = false;
          this.paymentStatusError = 'Could not confirm payment automatically. Please refresh your order status later.';
        }
      });
    };
    check(1);
  }

  private loadRazorpayScript(): Promise<void> {
    if ((window as any).Razorpay) { return Promise.resolve(); }
    if (!this.razorpayScriptPromise) {
      this.razorpayScriptPromise = new Promise<void>((resolve, reject) => {
        const script = document.createElement('script');
        script.src = 'https://checkout.razorpay.com/v1/checkout.js';
        script.async = true;
        script.onload = () => resolve();
        script.onerror = () => { script.remove(); this.razorpayScriptPromise = null; reject(); };
        document.head.appendChild(script);
      });
    }
    return this.razorpayScriptPromise;
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
      if (order.paymentMethod !== 'Razorpay') { this.createUpiPaymentQr(order); }
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
    this.loadOrdersPage(this.ordersPage);
    this.storeApi.getProducts().subscribe(products => {
      this.adminProducts = products;
    }, error => {
      this.handleAdminDataError(error, 'Could not load the product collection.');
    });
  }

  get ordersPageCount(): number {
    return Math.ceil(this.ordersTotal / this.ordersPageSize);
  }

  showOrderHistory(): void {
    this.selectedAdminOrder = null;
    this.loadOrdersPage(2);
  }

  showRecentOrders(): void {
    this.selectedAdminOrder = null;
    this.loadOrdersPage(1);
  }

  loadOrdersPage(page: number): void {
    if (page < 1 || (this.ordersTotal > 0 && page > this.ordersPageCount)) {
      return;
    }
    const requestId = ++this.ordersRequestId;
    this.loadingOrders = true;
    this.storeApi.getOrders(page).subscribe(result => {
      if (requestId !== this.ordersRequestId) { return; }
      this.orders = result.items;
      this.ordersTotal = result.totalCount;
      this.ordersPageSize = result.pageSize;
      this.ordersPage = result.page;
      this.loadingOrders = false;
      if (this.selectedAdminOrder) {
        this.selectedAdminOrder = result.items.find(order => order.id === this.selectedAdminOrder.id) || null;
      }
    }, error => {
      if (requestId !== this.ordersRequestId) { return; }
      this.loadingOrders = false;
      this.handleAdminDataError(error, 'Could not load orders. Please try again.');
    });
  }

  private handleAdminDataError(error: any, fallback: string): void {
    if (error.status === 401) {
      this.adminAuthenticated = false;
      this.dismissAdminToast();
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
      this.showAdminToast(product.name + ' added to the collection.');
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
      this.showAdminToast(updated.name + ' updated.');
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
      this.showAdminToast(product.name + ' removed from the collection.');
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
      this.showAdminToast('Order #' + order.id + ' payment marked ' + updated.paymentStatus + '.');
      this.loadAdminData();
    }, error => {
      this.errorMessage = this.messageFrom(error, 'Could not update the payment status.');
    });
  }

  advanceFulfillment(order: StoreOrder): void {
    if (order.paymentStatus !== 'Paid' || order.fulfillmentStatus === 'Delivered') { return; }
    const next = order.fulfillmentStatus === 'Dispatched' ? 'Delivered' : 'Dispatched';
    let trackingId = order.trackingId || '';
    if (next === 'Dispatched') {
      trackingId = this.dispatchTrackingId.trim();
      if (!trackingId || trackingId.length > 100) {
        this.errorMessage = 'Enter a tracking ID of up to 100 characters.';
        return;
      }
    } else if (!window.confirm('Mark order #' + order.id + ' as delivered?')) { return; }
    this.storeApi.updateFulfillment(order.id, next, trackingId).subscribe(updated => {
      order.fulfillmentStatus = updated.fulfillmentStatus;
      order.trackingId = updated.trackingId;
      this.showAdminToast('Order #' + order.id + ' marked ' + next + '.');
      this.loadAdminData();
    }, error => {
      this.errorMessage = this.messageFrom(error, 'Could not update delivery status.');
    });
  }

  openAdminOrderDetails(order: StoreOrder): void {
    this.selectedAdminOrder = order;
    this.dispatchTrackingId = order.trackingId || '';
    this.errorMessage = '';
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
