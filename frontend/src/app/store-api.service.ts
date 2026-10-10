import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { environment } from '../environments/environment';

export interface Product {
  id: number;
  name: string;
  description: string;
  price: number;
  image: string;
  imageUrl?: string | null;
  category: string;
  stock: number;
}

export interface CartLine {
  product: Product;
  quantity: number;
}

export interface OrderItem {
  id: number;
  orderId: number;
  productId: number;
  productName: string;
  quantity: number;
  unitPrice: number;
}

export interface StoreOrder {
  id: number;
  customerName: string;
  email: string;
  mobileNumber?: string | null;
  address: string;
  paymentStatus: string;
  paymentMethod: 'UPI' | 'Razorpay';
  fulfillmentStatus?: string;
  trackingId?: string | null;
  razorpayPaymentId?: string | null;
  createdAt: string;
  paymentReference?: string | null;
  paymentReportedAt?: string | null;
  total: number;
  items: OrderItem[];
}

export interface OrderPage {
  items: StoreOrder[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface CreatedStoreOrder extends StoreOrder {
  accessToken: string;
}

export interface AdminSummary {
  totalOrders: number;
  revenue: number;
  pendingPayments: number;
  productCount: number;
}

export interface AdminSession {
  userName: string;
  accessToken: string;
}

export interface CustomerProfile {
  id: number;
  name: string;
  email: string;
  mobileNumber: string | null;
  photoUrl: string | null;
  deliveryAddress: string | null;
}

export interface CustomerLogin {
  accessToken: string;
  customer: CustomerProfile;
}

@Injectable({ providedIn: 'root' })
export class StoreApiService {
  private readonly apiUrl = environment.apiUrl;
  private adminToken = '';
  private customerToken = '';

  constructor(private http: HttpClient) {
    try { this.adminToken = sessionStorage.getItem('sapana-admin-session') || ''; }
    catch (error) { /* Session storage may be disabled. */ }
    try { this.customerToken = sessionStorage.getItem('sapana-customer-session') || ''; }
    catch (error) { /* Session storage may be disabled. */ }
  }

  private adminHeaders(): HttpHeaders {
    return new HttpHeaders({ Authorization: 'Bearer ' + this.adminToken });
  }

  private customerHeaders(): HttpHeaders {
    return new HttpHeaders({ Authorization: 'Bearer ' + this.customerToken });
  }

  hasCustomerSession(): boolean { return !!this.customerToken; }

  clearCustomerSession(): void {
    this.customerToken = '';
    try { sessionStorage.removeItem('sapana-customer-session'); }
    catch (error) { /* Storage may be disabled. */ }
  }

  googleConfig(): Observable<{ clientId: string }> {
    return this.http.get<{ clientId: string }>(this.apiUrl + '/customer/google/config');
  }

  customerGoogleLogin(credential: string): Observable<CustomerLogin> {
    return this.http.post<CustomerLogin>(this.apiUrl + '/customer/google/login', { credential: credential }).pipe(
      map(result => {
        this.customerToken = result.accessToken;
        try { sessionStorage.setItem('sapana-customer-session', result.accessToken); }
        catch (error) { /* Keep the session in memory. */ }
        return result;
      }));
  }

  customerMe(): Observable<CustomerProfile> {
    return this.http.get<CustomerProfile>(this.apiUrl + '/customer/me', { headers: this.customerHeaders() });
  }

  updateCustomerDetails(name: string, mobileNumber: string, deliveryAddress: string): Observable<CustomerProfile> {
    return this.http.put<CustomerProfile>(this.apiUrl + '/customer/me',
      { name: name, mobileNumber: mobileNumber || null, deliveryAddress: deliveryAddress }, { headers: this.customerHeaders() });
  }

  customerLogout(): Observable<void> {
    return this.http.post<void>(this.apiUrl + '/customer/logout', {}, { headers: this.customerHeaders() });
  }

  myOrders(page: number = 1): Observable<OrderPage> {
    return this.http.get<OrderPage>(this.apiUrl + '/orders/mine',
      { headers: this.customerHeaders(), params: { page: String(page) } });
  }

  claimGuestOrder(orderId: number, accessToken: string): Observable<void> {
    return this.http.post<void>(this.apiUrl + '/orders/' + orderId + '/claim',
      { accessToken: accessToken }, { headers: this.customerHeaders() });
  }

  updateFulfillment(orderId: number, status: string, trackingId?: string): Observable<{ fulfillmentStatus: string; trackingId: string | null }> {
    return this.http.post<{ fulfillmentStatus: string; trackingId: string | null }>(
      this.apiUrl + '/admin/orders/' + orderId + '/fulfillment',
      { status: status, trackingId: trackingId || null }, { headers: this.adminHeaders() });
  }

  private saveAdminToken(token: string): void {
    this.adminToken = token;
    try { sessionStorage.setItem('sapana-admin-session', token); }
    catch (error) { /* Keep the session in memory when storage is unavailable. */ }
  }

  clearAdminToken(): void {
    this.adminToken = '';
    try { sessionStorage.removeItem('sapana-admin-session'); }
    catch (error) { /* Session storage may be disabled. */ }
  }

  getProducts(): Observable<Product[]> {
    const apiOrigin = new URL(this.apiUrl).origin;
    return this.http.get<Product[]>(this.apiUrl + '/products').pipe(map(products =>
      products.map(product => ({
        ...product,
        imageUrl: product.imageUrl ? new URL(product.imageUrl, apiOrigin).toString() : null
      }))
    ));
  }

  createProduct(product: Omit<Product, 'id' | 'imageUrl'>, imageFile?: File): Observable<Product> {
    const formData = new FormData();
    formData.append('name', product.name);
    formData.append('description', product.description);
    formData.append('price', String(product.price));
    formData.append('image', product.image);
    formData.append('category', product.category);
    formData.append('stock', String(product.stock));
    if (imageFile) {
      formData.append('imageFile', imageFile, imageFile.name);
    }
    return this.http.post<Product>(this.apiUrl + '/admin/products', formData, { headers: this.adminHeaders() });
  }

  updateProduct(productId: number, product: Omit<Product, 'id' | 'imageUrl'>, imageFile?: File): Observable<Product> {
    const formData = new FormData();
    formData.append('name', product.name);
    formData.append('description', product.description);
    formData.append('price', String(product.price));
    formData.append('image', product.image);
    formData.append('category', product.category);
    formData.append('stock', String(product.stock));
    if (imageFile) {
      formData.append('imageFile', imageFile, imageFile.name);
    }
    return this.http.put<Product>(this.apiUrl + '/admin/products/' + productId, formData,
      { headers: this.adminHeaders() });
  }

  deleteProduct(productId: number): Observable<void> {
    return this.http.delete<void>(this.apiUrl + '/admin/products/' + productId, { headers: this.adminHeaders() });
  }

  getOrders(page: number = 1): Observable<OrderPage> {
    return this.http.get<OrderPage>(this.apiUrl + '/orders', {
      headers: this.adminHeaders(), params: { page: String(page) }
    });
  }

  getSummary(): Observable<AdminSummary> {
    return this.http.get<AdminSummary>(this.apiUrl + '/admin/summary', { headers: this.adminHeaders() });
  }

  adminLogin(userName: string, password: string): Observable<AdminSession> {
    return this.http.post<AdminSession>(
      this.apiUrl + '/admin/auth/login',
      { userName: userName, password: password }
    ).pipe(map(session => {
      this.saveAdminToken(session.accessToken);
      return session;
    }));
  }

  getAdminSession(): Observable<{ userName: string }> {
    return this.http.get<{ userName: string }>(this.apiUrl + '/admin/auth/session', { headers: this.adminHeaders() });
  }

  updateAdminCredentials(credentials: {
    currentPassword: string;
    newUserName: string;
    newPassword: string;
  }): Observable<AdminSession> {
    return this.http.post<AdminSession>(
      this.apiUrl + '/admin/auth/credentials',
      credentials,
      { headers: this.adminHeaders() }
    ).pipe(map(session => {
      this.saveAdminToken(session.accessToken);
      return session;
    }));
  }

  adminLogout(): Observable<void> {
    return this.http.post<void>(this.apiUrl + '/admin/auth/logout', {}, { headers: this.adminHeaders() })
      .pipe(map(result => {
        this.clearAdminToken();
        return result;
      }));
  }

  createOrder(order: {
    customerName: string;
    email: string;
    mobileNumber: string;
    address: string;
    paymentMethod: 'UPI' | 'Razorpay';
    items: { productId: number; quantity: number }[];
  }): Observable<CreatedStoreOrder> {
    return this.http.post<CreatedStoreOrder>(this.apiUrl + '/orders', order,
      this.customerToken ? { headers: this.customerHeaders() } : {});
  }

  razorpayAvailability(): Observable<{ enabled: boolean }> {
    return this.http.get<{ enabled: boolean }>(this.apiUrl + '/razorpay/availability');
  }

  startRazorpay(orderId: number, accessToken: string): Observable<{ keyId: string; orderId: string; amount: number; currency: string }> {
    return this.http.post<{ keyId: string; orderId: string; amount: number; currency: string }>(
      this.apiUrl + '/razorpay/orders/' + orderId + '/checkout', { accessToken: accessToken });
  }

  verifyRazorpay(orderId: number, accessToken: string, paymentId: string, signature: string): Observable<{ paymentStatus: string }> {
    return this.http.post<{ paymentStatus: string }>(this.apiUrl + '/razorpay/orders/' + orderId + '/verify',
      { accessToken: accessToken, paymentId: paymentId, signature: signature });
  }

  getCustomerOrderStatus(orderId: number, accessToken: string): Observable<StoreOrder> {
    return this.http.post<StoreOrder>(this.apiUrl + '/orders/' + orderId + '/payment-status', {
      accessToken: accessToken
    });
  }

  reportUpiPayment(orderId: number, accessToken: string, transactionReference: string): Observable<StoreOrder> {
    return this.http.post<StoreOrder>(this.apiUrl + '/orders/' + orderId + '/payment-reported', {
      accessToken: accessToken,
      transactionReference: transactionReference.trim() || null
    });
  }

  cancelOrder(orderId: number, accessToken: string): Observable<StoreOrder> {
    return this.http.post<StoreOrder>(this.apiUrl + '/orders/' + orderId + '/cancel', {
      accessToken: accessToken
    });
  }

  updatePaymentStatus(orderId: number, paymentStatus: string): Observable<StoreOrder> {
    return this.http.post<StoreOrder>(
      this.apiUrl + '/admin/orders/' + orderId + '/payment-status',
      { paymentStatus: paymentStatus },
      { headers: this.adminHeaders() }
    );
  }
}
